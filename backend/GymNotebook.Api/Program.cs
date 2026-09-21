using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

// This file uses "top-level statements": no Program class, no Main method. The compiler
// generates both around this code. Everything up to app.Run() executes once at startup,
// in two phases — configure services (builder.*), then configure the HTTP pipeline (app.*).

// ---------------------------------------------------------------------------------------
// Phase 1: the builder. Collects configuration (appsettings.json, appsettings.{Env}.json,
// user-secrets in Development, environment variables — later sources override earlier
// ones) and the dependency-injection container that every handler will pull from.
// ---------------------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

// Locally this comes from `dotnet user-secrets` as "ConnectionStrings:Default"; in a
// container it's the env var ConnectionStrings__Default (the "__" maps to ":"). Without
// the `?? throw`, a missing or mistyped key would give a null connection string, the app
// would start cleanly, and the first query would fail — failing at boot with the key
// named is far cheaper to debug.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

// Jwt:Secret signs every token, so it's as sensitive as the database password and
// lives in the same places (user-secrets locally, a Container Apps secret in Azure).
// Jwt:ExpiryMinutes isn't sensitive and has a committed default in
// appsettings.Development.json, so no `?? throw` for it.
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");
var jwtExpiryMinutes = builder.Configuration.GetValue<int>("Jwt:ExpiryMinutes");
// Unset or empty means registration is open (see PLAN.md, Configuration), so absence is
// a valid state and there's no `?? throw` — the deploy checklist, not the code, is what
// makes sure it's set in Azure.
var inviteCode = builder.Configuration["INVITE_CODE"];

// Comma-separated origins the browser may call this API from — the Vite dev server now, the
// deployed frontend URL later. An origin is scheme + host + port with no trailing slash
// (http://localhost:5173/ silently matches nothing), and it's localhost even inside Compose:
// the value is compared against what the browser sends, so Host=db-style service names
// don't apply. Empty is never a valid state — unlike INVITE_CODE — and Compose turns a missing
// .env entry into "" rather than absent, so the guard check the split result, not just null.
var corsOrigins = builder.Configuration["CORS_ORIGINS"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (corsOrigins is not { Length: > 0 })
{
    throw new InvalidOperationException("CORS_ORIGINS is not configured.");
}

// The two-argument GetValue returns the fallback when the key is absent, so these
// defaults are what production runs with. Tests shrink them (RateLimitedGymNotebookFactory)
// to hit the limit in three requests.
var rateLimitPermitLimit = builder.Configuration.GetValue("RateLimit:PermitLimit", 10);
var rateLimitWindowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 60);

// Register AppDbContext with the Npgsql (PostgreSQL) provider. AddDbContext uses a
// *scoped* lifetime: one AppDbContext per HTTP request, created when a handler asks
// for it and disposed when the response is done. EF Core itself is database-agnostic;
// UseNpgsql is what makes it speak Postgres SQL and types. UseSnakeCaseNamingConvention
// translates the PascalCase C# properties (e.g. TokenVersion) into the snake_case
// columns PLAN.md's data model is written in (token_version) — Postgres's own idiom,
// and the only way to get unquoted, case-insensitive column names out of EF Core.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

// Registers the JWT bearer handler as the default scheme: every endpoint behind
// .RequireAuthorization() expects an `Authorization: Bearer <token>` header, and a
// missing or invalid token becomes a 401 with no handler code involved.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Issuer and audience are never written into the token (see JwtTokenFactory), so
        // validating them would reject every token. Signature and lifetime are the two
        // checks that matter: the same secret that signed the token verifies it here.
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        };

        // Without this, the handler silently renames short claim names to long legacy URIs
        // on the way in ("sub" becomes ClaimTypes.NameIdentifier) — a well-known gotcha that
        // makes FindFirst("sub") return null even though the claim is right there in the
        // token. Turning it off keeps claim names exactly as JwtTokenFactory wrote them.
        options.MapInboundClaims = false;

        options.Events = new JwtBearerEvents
        {
            // Runs only after the token's signature and expiry already checked out. This is
            // where token_version — the one thing about a token that can change after it's
            // issued (see PLAN.md, Auth section) — gets enforced, since that's inherently a
            // database lookup and standard JWT validation has no way to do that on its own.
            OnTokenValidated = async context =>
            {
                var subClaim = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                var tvClaim = context.Principal?.FindFirst("tv")?.Value;

                if (!int.TryParse(subClaim, out var userId) || tvClaim is null)
                {
                    context.Fail("Invalid token claims.");
                    return;
                }

                // AddJwtBearer's options are configured once at startup, so AppDbContext
                // (scoped per request) can't be injected here directly — it has to be
                // resolved from this request's own service provider instead.
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var user = await db.Users.FindAsync([userId], context.HttpContext.RequestAborted);

                // A missing user or a version mismatch both mean the same thing: this token
                // should no longer work, even though it hasn't expired. Fail() overrides the
                // otherwise-successful validation, which is what turns this into the normal
                // 401 anything requiring authorization already returns for bad credentials.
                if (user is null || user.TokenVersion.ToString() != tvClaim)
                {
                    context.Fail("Token has been revoked.");
                }
            },
        };
    });

// The services behind .RequireAuthorization(). No named policies: "any authenticated
// user" is the only rule this API has.
builder.Services.AddAuthorization();

// One named "auth" policy, applied only to the endpoints that opt in with
// .RequireRateLimiting("auth") — login and register (see PLAN.md, Rate limiting).
builder.Services.AddRateLimiter(options =>
{
    // The default rejection status is 503, which reads as "server broken"; 429 tells the
    // caller the problem is them.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A fixed window per client IP: each address gets PermitLimit requests per Window,
    // then is rejected until the window resets. QueueLimit = 0 refuses excess requests
    // immediately instead of parking them until a permit frees up. Counters live in
    // process — correct with one replica, and the thing that needs shared state if this
    // ever scales out.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
                QueueLimit = 0,
            }));
});

// One policy for every endpoint, so it's the *default* policy and app.UseCors() below
// needs no name to keep in sync with this one. WithOrigins is the allow-list from
// CORS_ORIGINS; the browser compares its Origin header against it exactly, scheme, host
// and port. AllowAnyHeader and AllowAnyMethod are both needed for the real traffic, not
// out of laziness: Authorization and Content-Type: application/json are "non-simple"
// headers, and PATCH/PUT/DELETE are non-simple methods, and each one makes the browser
// send a preflight OPTIONS that this policy has to answer yes to. No AllowCredentials —
// that flag is about cookies, and the token travels in the Authorization header, which
// is just a header as far as CORS is concerned. Adding it would also rule out a wildcard
// origin later, for nothing.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Microsoft.AspNetCore.OpenApi inspects the mapped endpoints and their metadata
// (.WithSummary, .Produces<T> etc. below) and builds the OpenAPI document from them.
// A document transformer is a hook that edits the finished document before it's served.
// The first one sets the title; the second registers the JWT Bearer security scheme so
// protected routes can be called from the Scalar page.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Gym Notebook API";
        document.Info.Description = "Digital replacement for a paper gym log.";
        return Task.CompletedTask;
    });

    // Registers the JWT Bearer scheme on the document so Scalar renders an "Authorize"
    // button. This only describes the scheme — it doesn't mark any operation as needing
    // it; the operation transformer below does that per-endpoint.
    options.AddDocumentTransformer((document, _, _) =>
    {
        // Both collections are null until something populates them — Microsoft.OpenApi
        // doesn't pre-initialize its optional properties the way you might expect.
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        return Task.CompletedTask;
    });

    // Only endpoints actually behind .RequireAuthorization() (like /auth/me) get marked
    // as requiring the Bearer scheme — checking EndpointMetadata for IAuthorizeData is
    // how the generator knows which those are, since Minimal APIs has no attribute to
    // reflect on the way MVC controllers would. Without this, Scalar would either lock
    // every endpoint (including /health and /auth/login, which take no token at all) or
    // none of them, both of which are wrong documentation.
    options.AddOperationTransformer((operation, context, _) =>
    {
        var requiresAuth = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Any();

        if (requiresAuth)
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = new List<string>(),
            });
        }

        return Task.CompletedTask;
    });
});

// ---------------------------------------------------------------------------------------
// Phase 2: the app. Build() freezes the service container; from here on we're wiring
// the request pipeline — middleware runs in the order it's added, endpoints last.
// ---------------------------------------------------------------------------------------
var app = builder.Build();

// `dotnet GymNotebook.Api.dll --migrate`: apply pending migrations and exit without
// starting the server. entrypoint.sh runs this before the real app, because the runtime
// image has no SDK for `dotnet ef`. Same MigrateAsync the test fixture calls.
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    return;
}

// Dev-only: /openapi/v1.json (the document) and /scalar (the interactive UI that reads
// it). The deployed app has no business publishing its own surface to whoever finds the
// URL. WebApplicationFactory in the tests runs as Development, so the tests see these too.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Order matters. CORS goes first: a preflight OPTIONS carries no bearer token, and the
// CORS middleware answers it itself (204) and short-circuits — if authentication ran
// first, /auth/me's preflight would 401 and the browser would never send the real
// request. Being ahead of the rate limiter also means preflights to /auth/login don't
// spend the auth budget. Then authentication reads the bearer token into
// HttpContext.User, and authorization checks that user against each endpoint's
// requirements. The rate limiter is endpoint-aware (RequireRateLimiting is endpoint
// metadata), so it must come after routing — which WebApplication adds implicitly ahead
// of all four of these.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The first endpoint, and the annotation pattern every later endpoint follows.
//
// Handler parameters are resolved by Minimal APIs: AppDbContext comes from DI (the
// scoped instance for this request), CancellationToken is the request's — it fires if
// the client disconnects, so the DB call is abandoned rather than completed for nobody.
//
// db.Database.CanConnectAsync() is the cheapest possible round trip (a SELECT 1) — no
// entities, no tables of ours involved. It's the "is the database there" probe that
// Compose and Azure health checks will hit.
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new HealthResponse("ok", "reachable"))
        : Results.Json(new HealthResponse("degraded", "unreachable"), statusCode: StatusCodes.Status503ServiceUnavailable))
   // Everything below is metadata, not behaviour: it feeds the OpenAPI document.
   .WithName("GetHealth")                                            // operationId; also usable for link generation
   .WithTags("Health")                                               // groups endpoints in the Scalar sidebar
   .WithSummary("Liveness and database reachability")
   .WithDescription("Returns 200 when the API is up and the database answers, 503 when the database is unreachable. Used by Compose and Azure probes.")
   // The generator can infer the 200 from a simple handler but never an alternative
   // status code, so every status the handler can return is declared explicitly. The
   // response type is a record (HealthResponse) rather than an anonymous object because
   // an anonymous type has no name and comes out of the generator as an empty schema.
   .Produces<HealthResponse>()
   .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

// Every route mapped on `auth` gets the /auth prefix. A group is also the one place to
// hang metadata every auth endpoint shares, should any ever be needed.
var auth = app.MapGroup("/auth");

auth.MapPost("/register", async (RegisterRequest request, AppDbContext db, CancellationToken ct) =>
{
    // Empty/whitespace credentials would otherwise sail through to a BCrypt hash of "" or
    // a username no one could ever type again to log back in.
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest();
    }

    // inviteCode is the server's configured value (empty/unset = registration open); the
    // request carries what the caller supplied. A mismatch when a code IS required is a
    // 403, not a 401 — this isn't "who are you", it's "you're not allowed to sign up".
    if (!string.IsNullOrEmpty(inviteCode) && request.InviteCode != inviteCode)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    // Checked up front rather than relying solely on the DB's unique index, so a
    // duplicate username comes back as a clean 409 instead of an unhandled
    // DbUpdateException surfacing as a 500. (Two near-simultaneous registrations with the
    // same username could still both pass this check and race to the index — acceptable
    // here; the index is still what guarantees the row-level correctness.)
    if (await db.Users.AnyAsync(u => u.Username == request.Username, ct))
    {
        return Results.Conflict();
    }

    var user = new User
    {
        Username = request.Username,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        TokenVersion = 0,
    };

    // CreatedAt is deliberately left unset: EF Core recognizes the CLR default value on a
    // DateTimeOffset property and omits the column from the INSERT, letting the "now()"
    // column default configured in AppDbContext fill it in.
    db.Users.Add(user);
    await db.SaveChangesAsync(ct);

    var token = JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes);
    return Results.Ok(new AuthResponse(token));
}).WithName("RegisterUser")
   .WithSummary("Registers a new user")
   .WithDescription("Creates a new user account with the given username and password. Returns a JWT token for authentication.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status403Forbidden)
   .Produces(StatusCodes.Status409Conflict)
   .RequireRateLimiting("auth");

auth.MapPost("/login", async (LoginRequest request, AppDbContext db, CancellationToken ct) =>
{
    // Same guard as register. A blank password can never match a hash, so rejecting it
    // early just saves the DB lookup and the BCrypt round trip.
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest();
    }

    var user = await db.Users.SingleOrDefaultAsync(u => u.Username == request.Username, ct);

    // Same 401 whether the username doesn't exist or the password is wrong — a different
    // response for each would let a caller enumerate valid usernames by trying them
    // one at a time and watching which error comes back.
    if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var token = JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes);
    return Results.Ok(new AuthResponse(token));
}).WithName("LoginUser")
   .WithSummary("Logs in a user")
   .WithDescription("Authenticates a user with the given username and password. Returns a JWT token for authentication.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status401Unauthorized)
   .RequireRateLimiting("auth");

// The smallest possible protected route. ClaimsPrincipal is another parameter Minimal
// APIs knows how to supply: it's HttpContext.User, already populated by the bearer
// handler — and already past the token_version check in OnTokenValidated — by the time
// the handler runs.
//
// The username comes from the row, not the token: the JWT carries only "sub" (the id)
// and "tv", and adding a name claim would mean a token outliving a rename. One indexed
// lookup by primary key is cheap, and the cover page needs the name to greet its owner.
auth.MapGet("/me", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(user);

    // SingleAsync, not SingleOrDefaultAsync: OnTokenValidated has just loaded this row to
    // compare token_version, so a miss here is a bug, not a 404 — same reasoning as ParseUserId.
    var username = await db.Users
        .Where(u => u.Id == userId)
        .Select(u => u.Username)
        .SingleAsync(ct);

    return Results.Ok(new MeResponse(userId, username));
})
   .RequireAuthorization()
   .WithName("GetCurrentUser")
   .WithSummary("Returns the authenticated user's id and username")
   .WithDescription("Proves a bearer token is valid and its token_version hasn't been revoked. The username is what the frontend shows on the cover page.")
   .Produces<MeResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status401Unauthorized);

// Requires a valid token *and* the current password: a stolen token alone shouldn't be
// enough to change the password and lock the real owner out.
auth.MapPost("/change-password", async (ChangePasswordRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
    {
        return Results.BadRequest();
    }

    var userId = ParseUserId(caller);
    var user = await db.Users.FindAsync([userId], ct);

    // Same 401 as login for a wrong current password. The `user is null` branch is
    // defensive — OnTokenValidated already rejects tokens for users that no longer exist —
    // but FindAsync returns a nullable, so the compiler wants it handled either way.
    if (user is null || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    // Both changes go out in the one SaveChangesAsync, so either the new hash and the
    // version bump both land or neither does. The bump is what revokes every token
    // issued so far (see PLAN.md, Auth section); the fresh token minted below carries the
    // new version, so the caller who made the change isn't logged out by it.
    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
    user.TokenVersion++; // invalidate all existing tokens
    await db.SaveChangesAsync(ct);

    var token = JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes);
    return Results.Ok(new AuthResponse(token));
})
   .RequireAuthorization()
   .WithName("ChangePassword")
   .WithSummary("Changes the authenticated user's password")
   .WithDescription("Updates the authenticated user's password after verifying the current password.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status401Unauthorized);

var exercises = app.MapGroup("/exercises").RequireAuthorization();

exercises.MapGet("/", async (string? search, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var query = db.Exercises.Where(e => e.UserId == userId);

    if (!string.IsNullOrWhiteSpace(search))
    {
        var normalizedSearch = ExerciseNameNormalizer.Normalize(search);
        query = query.Where(e => e.NormalizedName.Contains(normalizedSearch));
    }

    var results = await ProjectExerciseResponses(db, query.OrderBy(e => e.Name)).ToListAsync(ct);

    return Results.Ok(results);
})
   .WithName("SearchExercises")
   .WithSummary("Searches the caller's exercises")
   .WithDescription("Exercise-index and autocomplete lookup scoped to the authenticated user. Returns every exercise when search is omitted. Each result carries its distinct session count and most recently logged set (null if none): the latest session's last non-warm-up set, or its last warm-up set if that session had nothing else.")
   .Produces<List<ExerciseResponse>>(StatusCodes.Status200OK);

exercises.MapGet("/{id:int}/history", async (int id, DateOnly? from, DateOnly? to, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);

    if (from.HasValue && to.HasValue && from > to)
    {
        return Results.BadRequest();
    }

    // Scope the lookup to the caller so an unknown id and another user's id are
    // indistinguishable, matching every other ownership check in this API.
    var exercise = await db.Exercises
        .AsNoTracking()
        .SingleOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);

    if (exercise is null)
    {
        return Results.NotFound();
    }

    // Project only the fields needed to choose and explain each point. This is one
    // database round trip; grouping happens after materialization because the chosen
    // source set (weight + reps) must remain attached to the calculated maximum.
    var candidatesQuery =
        from setEntry in db.SetEntries.AsNoTracking()
        join block in db.WorkoutExercises.AsNoTracking() on setEntry.WorkoutExerciseId equals block.Id
        join workout in db.Workouts.AsNoTracking() on block.WorkoutId equals workout.Id
        where block.ExerciseId == exercise.Id
              && workout.UserId == userId
              && !setEntry.IsWarmup
              // An added-weight bodyweight set is a different metric from plain reps;
              // PLAN.md deliberately defers charting that mixed load.
              && (exercise.IsBodyweight ? setEntry.Weight == null : setEntry.Weight != null)
        select new
        {
            WorkoutId = workout.Id,
            workout.Date,
            workout.StartedAt,
            SetEntryId = setEntry.Id,
            setEntry.Weight,
            setEntry.Reps,
        };

    if (from.HasValue)
    {
        candidatesQuery = candidatesQuery.Where(candidate => candidate.Date >= from.Value);
    }

    if (to.HasValue)
    {
        candidatesQuery = candidatesQuery.Where(candidate => candidate.Date <= to.Value);
    }

    var candidates = await candidatesQuery.ToListAsync(ct);

    // A workout is one chart point even when the exercise appears in more than one
    // block. Workouts on the same calendar date are intentionally separate groups.
    var points = candidates
        .Select(candidate => new
        {
            Candidate = candidate,
            Value = ProgressMetric.Calculate(exercise.IsBodyweight, candidate.Weight, candidate.Reps)!.Value,
        })
        .GroupBy(item => new
        {
            item.Candidate.WorkoutId,
            item.Candidate.Date,
            item.Candidate.StartedAt,
        })
        .Select(group => group
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Candidate.SetEntryId)
            .First())
        .OrderBy(item => item.Candidate.Date)
        .ThenBy(item => item.Candidate.StartedAt)
        .ThenBy(item => item.Candidate.WorkoutId)
        .Select(item => new ExerciseHistoryPointResponse(
            item.Candidate.WorkoutId,
            item.Candidate.Date,
            item.Candidate.StartedAt,
            item.Candidate.Weight,
            item.Candidate.Reps,
            item.Value))
        .ToList();

    return Results.Ok(new ExerciseHistoryResponse(exercise.Id, exercise.Name, exercise.IsBodyweight, points));
})
   .WithName("GetExerciseHistory")
   .WithSummary("Gets an exercise's progress history")
   .WithDescription("Returns the best qualifying working set per workout, oldest first. Loaded exercises use Epley e1RM (with tested singles unchanged); bodyweight exercises use reps and exclude added-weight sets. Optional from/to calendar dates are inclusive.")
   .Produces<ExerciseHistoryResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status404NotFound);

exercises.MapPatch("/{id:int}", async (int id, UpdateExerciseRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);

    if (exercise is null)
    {
        return Results.NotFound();
    }

    if (!string.IsNullOrWhiteSpace(request.Name))
    {
        var normalizedName = ExerciseNameNormalizer.Normalize(request.Name);

        // Only a display-text change (casing/whitespace) when the normalized form
        // is unchanged — nothing else can be colliding with a normalized name this
        // row already owns, so there's nothing to merge.
        if (normalizedName != exercise.NormalizedName)
        {
            // Renaming onto a name that already belongs to another of this user's
            // exercises merges them (see PLAN.md) instead of failing on the unique
            // (user_id, normalized_name) index: the exercise being PATCHed survives,
            // the other one's blocks are re-pointed onto it, and its row is deleted.
            var collision = await db.Exercises.SingleOrDefaultAsync(
                e => e.UserId == userId && e.NormalizedName == normalizedName && e.Id != id, ct);

            if (collision is not null)
            {
                // Re-pointed as tracked entities rather than a bulk ExecuteUpdateAsync,
                // so this reassignment and the delete below land in the same
                // SaveChangesAsync transaction below — a failure partway through
                // rolls back both instead of leaving the merge half-applied.
                var collisionBlocks = await db.WorkoutExercises
                    .Where(we => we.ExerciseId == collision.Id)
                    .ToListAsync(ct);

                foreach (var block in collisionBlocks)
                {
                    block.ExerciseId = exercise.Id;
                }

                db.Exercises.Remove(collision);
            }

            exercise.NormalizedName = normalizedName;
        }

        exercise.Name = request.Name;
    }

    if (request.IsBodyweight.HasValue)
    {
        exercise.IsBodyweight = request.IsBodyweight.Value;
    }

    await db.SaveChangesAsync(ct);

    // Re-read through the shared projection rather than building the response by hand, so
    // LastSet comes back the same way it does from GET — including the sets a merge has
    // just re-pointed onto this exercise.
    var response = await ProjectExerciseResponses(db, db.Exercises.Where(e => e.Id == exercise.Id)).SingleAsync(ct);
    return Results.Ok(response);
})
   .WithName("UpdateExercise")
   .WithSummary("Updates an existing exercise")
   .WithDescription("Modifies the name and/or bodyweight status of an existing exercise. Only the owner can update their exercises.")
   .Produces<ExerciseResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status404NotFound);

var workouts = app.MapGroup("/workouts").RequireAuthorization();

workouts.MapPost("/", async (CreateWorkoutRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);

    var workout = new Workout
    {
        UserId = userId,
        Date = request.Date,
        // PostgreSQL timestamptz stores an instant rather than its original offset,
        // and Npgsql requires DateTimeOffset values to have offset zero. Accept any
        // valid offset at the HTTP boundary, then normalize it before persistence.
        StartedAt = request.StartedAt.ToUniversalTime(),
        Title = request.Title,
        BodyweightKg = request.BodyweightKg,
        Location = request.Location,
        Notes = request.Notes,
    };

    db.Workouts.Add(workout);
    await db.SaveChangesAsync(ct);

    var response = new WorkoutDetailResponse(workout.Id, workout.Date, workout.StartedAt, workout.EndedAt,
        workout.Title, workout.BodyweightKg, workout.Location, workout.Notes, Exercises: []);

    return Results.Created($"/workouts/{workout.Id}", response);
})
   .WithName("CreateWorkout")
   .WithSummary("Creates a new workout")
   .WithDescription("Starts a new session page for the authenticated user.")
   .Produces<WorkoutDetailResponse>(StatusCodes.Status201Created);

workouts.MapGet("/", async (int? limit, int? before, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);

    // Bounds the page size: unset falls back to a sane default, and nothing lets a
    // caller request an unbounded page by passing an absurd limit.
    const int DefaultLimit = 20;
    const int MaxLimit = 100;
    var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    var query = db.Workouts.Where(w => w.UserId == userId);

    if (before.HasValue)
    {
        var anchor = await db.Workouts.SingleOrDefaultAsync(w => w.Id == before && w.UserId == userId, ct);
        if (anchor is null)
        {
            // A cursor that doesn't exist or isn't the caller's own workout is a bad
            // request, not "no results" — silently starting over from page one would
            // hide the real problem from the client.
            return Results.BadRequest();
        }

        // Keyset pagination: "everything earlier than the anchor" under the same
        // (date desc, started_at desc) ordering the page itself uses.
        query = query.Where(w =>
            w.Date < anchor.Date ||
            (w.Date == anchor.Date && w.StartedAt < anchor.StartedAt));
    }

    // The counts and names are projected inside the same query rather than loaded per
    // row: EF Core translates the nested collections into one SQL statement, so a page of
    // twenty summaries is still one round trip. ExerciseNames needs a subquery because
    // WorkoutExercise has no Exercise navigation property (only the raw ExerciseId).
    var results = await query
        .OrderByDescending(w => w.Date)
        .ThenByDescending(w => w.StartedAt)
        .Take(take)
        .Select(w => new WorkoutSummaryResponse(
            w.Id,
            w.Date,
            w.StartedAt,
            w.EndedAt,
            w.Title,
            w.WorkoutExercises.Count,
            w.WorkoutExercises.Sum(we => we.SetEntries.Count),
            w.WorkoutExercises
                .OrderBy(we => we.Position)
                .Select(we => db.Exercises.Where(e => e.Id == we.ExerciseId).Select(e => e.Name).Single())
                .ToList()))
        .ToListAsync(ct);

    return Results.Ok(results);
})
   .WithName("ListWorkouts")
   .WithSummary("Lists the caller's workouts")
   .WithDescription("Pages newest first (date desc, then started_at desc). `before` is the id of the last workout from the previous page. Each row carries the exercise names (in position order), exercise and set counts and the end time, so the list can render without fetching each workout.")
   .Produces<List<WorkoutSummaryResponse>>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest);

workouts.MapGet("/{id:int}", async (int id, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);

    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    var workoutExercises = await GetWorkoutExercisesAsync(db, workout.Id, ct);

    var response = new WorkoutDetailResponse(workout.Id, workout.Date, workout.StartedAt, workout.EndedAt,
        workout.Title, workout.BodyweightKg, workout.Location, workout.Notes, workoutExercises);

    return Results.Ok(response);
})
   .WithName("GetWorkout")
   .WithSummary("Retrieves a specific workout")
   .WithDescription("Returns the details of a single workout belonging to the authenticated user.")
   .Produces<WorkoutDetailResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status404NotFound);

workouts.MapPatch("/{id:int}", async (int id, UpdateWorkoutRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    // Date and StartedAt are required on a Workout, so an explicit JSON null is a
    // malformed update. Omitted properties remain unchanged; nullable properties
    // below deliberately accept explicit null so the client can clear them.
    if ((request.HasDate && !request.Date.HasValue) ||
        (request.HasStartedAt && !request.StartedAt.HasValue))
    {
        return Results.BadRequest();
    }

    if (request.HasDate)
    {
        workout.Date = request.Date!.Value;
    }

    if (request.HasStartedAt)
    {
        workout.StartedAt = request.StartedAt!.Value.ToUniversalTime();
    }

    if (request.HasEndedAt)
    {
        // Preserve explicit null (clear the end time), but normalize a supplied
        // instant for the same PostgreSQL timestamptz requirement as StartedAt.
        workout.EndedAt = request.EndedAt?.ToUniversalTime();
    }

    if (request.HasTitle)
    {
        workout.Title = request.Title;
    }

    if (request.HasBodyweightKg)
    {
        workout.BodyweightKg = request.BodyweightKg;
    }

    if (request.HasLocation)
    {
        workout.Location = request.Location;
    }

    if (request.HasNotes)
    {
        workout.Notes = request.Notes;
    }

    await db.SaveChangesAsync(ct);

    var workoutExercises = await GetWorkoutExercisesAsync(db, workout.Id, ct);

    var response = new WorkoutDetailResponse(workout.Id, workout.Date, workout.StartedAt, workout.EndedAt,
        workout.Title, workout.BodyweightKg, workout.Location, workout.Notes, workoutExercises);
    return Results.Ok(response);
})
    .WithName("UpdateWorkout")
    .WithSummary("Updates an existing workout")
    .WithDescription("Modifies only the supplied workout fields. Explicit null clears nullable fields; date and startedAt cannot be null. Only the owner can update their workouts.")
    .Produces<WorkoutDetailResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound);

workouts.MapDelete("/{id:int}", async (int id, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    db.Workouts.Remove(workout);
    await db.SaveChangesAsync(ct);

    return Results.NoContent();
})
   .WithName("DeleteWorkout")
   .WithSummary("Deletes a workout")
   .WithDescription("Deletes a workout and all of its exercises and sets.")
   .Produces(StatusCodes.Status204NoContent)
   .Produces(StatusCodes.Status404NotFound);

// The bulk session write: what the "new workout" page's single save button sends (see
// PLAN.md, REST API). Replace rather than diff — the client owns the whole block list,
// and a diff would need block ids the client deliberately never sees.
workouts.MapPut("/{id:int}/exercises", async (int id, PutWorkoutExercisesRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    // A body of `{}` deserializes to a null list, which would be an NRE (a 500) in the
    // loop below. Nullable reference types can't help here: the JSON deserializer writes
    // the property regardless of what the declaration promises.
    if (request.Exercises is null)
    {
        return Results.BadRequest();
    }

    // An explicit transaction rather than the usual one-SaveChangesAsync-is-one-transaction
    // trick, because this handler genuinely needs several saves: WorkoutExercise has no
    // Exercise *navigation property* (just the raw ExerciseId), so EF Core has no way to
    // fix up the foreign key of a block whose exercise was created in this same request —
    // the new Exercise has to reach the database and get its id before the block can point
    // at it. Saving as we go inside one transaction keeps PLAN.md's rule (the bulk write
    // rolls back whole, never half-applies) while still letting each new exercise be
    // visible to the next block's lookup, which is what makes the same new name appearing
    // twice in one payload resolve to one Exercise row instead of two.
    await using var transaction = await db.Database.BeginTransactionAsync(ct);

    // Replace means the old blocks go, all of them. Their SetEntry rows go with them
    // through the database-level cascade configured in AppDbContext, so there's no reason
    // to load the sets first. Flushed before the inserts so the delete can't be reordered
    // after rows that reuse the same (workout_id, exercise_id) pairing.
    var existingBlocks = await db.WorkoutExercises
        .Where(we => we.WorkoutId == workout.Id)
        .ToListAsync(ct);

    db.WorkoutExercises.RemoveRange(existingBlocks);
    await db.SaveChangesAsync(ct);

    var position = 0;

    foreach (var input in request.Exercises)
    {
        if (string.IsNullOrWhiteSpace(input.ExerciseName))
        {
            return Results.BadRequest();
        }

        var exercise = await GetOrCreateExerciseAsync(db, userId, input.ExerciseName, ct);

        // Always a brand-new block, never a reused one — including when the same exercise
        // name appears twice in the payload. Two blocks for one exercise is the "came back
        // to squats later in the session" case PLAN.md's data model exists to record, and
        // collapsing them into one block would silently destroy it.
        var block = new WorkoutExercise
        {
            WorkoutId = workout.Id,
            ExerciseId = exercise.Id,
            Position = position++,
        };

        var setNumber = 1;

        foreach (var set in input.Sets ?? [])
        {
            // Added through the navigation property rather than with an explicit
            // WorkoutExerciseId: the block has no id yet, and this is the one relationship
            // that *does* have a nav property for EF Core to fix up on save.
            block.SetEntries.Add(new SetEntry
            {
                SetNumber = setNumber++,
                Weight = set.Weight,
                Reps = set.Reps,
                IsWarmup = set.IsWarmup,
            });
        }

        db.WorkoutExercises.Add(block);
    }

    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    var workoutExercises = await GetWorkoutExercisesAsync(db, workout.Id, ct);

    var response = new WorkoutDetailResponse(workout.Id, workout.Date, workout.StartedAt, workout.EndedAt,
        workout.Title, workout.BodyweightKg, workout.Location, workout.Notes, workoutExercises);

    return Results.Ok(response);
})
   .WithName("ReplaceWorkoutExercises")
   .WithSummary("Replaces a workout's exercises and sets")
   .WithDescription("Atomically replaces every exercise block and set in the workout with the ones supplied. Exercise names are get-or-create; position and set number come from array order, never from the client.")
   .Produces<WorkoutDetailResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status404NotFound);

// The incremental counterpart to the bulk write: one set appended from the history view,
// without the client having to resend the whole session.
workouts.MapPost("/{id:int}/sets", async (int id, CreateSetRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    // A blank name isn't a range check — it normalizes to "" and would claim this user's
    // one and only "" slot in the unique (user_id, normalized_name) index with a row no
    // autocomplete could ever offer back. Same reasoning as register's blank-username guard.
    if (string.IsNullOrWhiteSpace(request.ExerciseName))
    {
        return Results.BadRequest();
    }

    // Same reasoning as the PUT above: the exercise and the block each need to exist in
    // the database before the next step can reference them by id, and a failure partway
    // through should not leave an empty block behind.
    await using var transaction = await db.Database.BeginTransactionAsync(ct);

    var exercise = await GetOrCreateExerciseAsync(db, userId, request.ExerciseName, ct);
    var block = await GetOrCreateBlockAsync(db, workout.Id, exercise.Id, ct);

    // Assigned server-side as the next number in this block, never trusted from the
    // client (PLAN.md, REST API). MaxAsync over a nullable projection so an empty block
    // yields null rather than throwing.
    var maxSetNumber = await db.SetEntries
        .Where(se => se.WorkoutExerciseId == block.Id)
        .MaxAsync(se => (int?)se.SetNumber, ct);

    var set = new SetEntry
    {
        WorkoutExerciseId = block.Id,
        SetNumber = (maxSetNumber ?? 0) + 1,
        Weight = request.Weight,
        Reps = request.Reps,
        IsWarmup = request.IsWarmup,
    };

    db.SetEntries.Add(set);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    // 201 with no Location header, deliberately: there is no GET /workouts/{id}/sets/{setId}
    // for one to point at, and inventing a URL that 404s is worse than omitting the header.
    // Results.Json rather than Results.Created for exactly that reason. The body is only
    // the new set, not the whole WorkoutDetailResponse — the caller already has the rest
    // of the page and this is an incremental add.
    return Results.Json(
        new SetEntryResponse(set.Id, set.SetNumber, set.Reps, set.Weight, set.IsWarmup),
        statusCode: StatusCodes.Status201Created);
})
   .WithName("AddWorkoutSet")
   .WithSummary("Appends a set to a workout")
   .WithDescription("Get-or-creates the exercise and its block in this workout, then appends the set at the next set number. Returns 201 with the new set; no Location header, since single sets have no GET route.")
   .Produces<SetEntryResponse>(StatusCodes.Status201Created)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status404NotFound);

workouts.MapPatch("/{id:int}/sets/{setId:int}", async (int id, int setId, UpdateSetRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    var set = await FindSetInWorkoutAsync(db, workout.Id, setId, ct);

    if (set is null)
    {
        return Results.NotFound();
    }

    // All three applied unconditionally — see UpdateSetRequest for why this one isn't the
    // sparse shape the other PATCH endpoints use. SetNumber and WorkoutExerciseId stay put:
    // moving a set between blocks or renumbering it is not what this route is for.
    set.Weight = request.Weight;
    set.Reps = request.Reps;
    set.IsWarmup = request.IsWarmup;

    await db.SaveChangesAsync(ct);

    return Results.Ok(new SetEntryResponse(set.Id, set.SetNumber, set.Reps, set.Weight, set.IsWarmup));
})
   .WithName("UpdateWorkoutSet")
   .WithSummary("Updates a set")
   .WithDescription("Replaces the weight, reps and warm-up flag of one set. All three are applied, so weight can be cleared by sending null.")
   .Produces<SetEntryResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status404NotFound);

workouts.MapDelete("/{id:int}/sets/{setId:int}", async (int id, int setId, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

    if (workout is null)
    {
        return Results.NotFound();
    }

    var set = await FindSetInWorkoutAsync(db, workout.Id, setId, ct);

    if (set is null)
    {
        return Results.NotFound();
    }

    // Nothing hangs off a SetEntry, so there's no cascade to think about — and the
    // surviving sets in the block keep their numbers rather than being renumbered, which
    // would change the ids the client is holding for rows it didn't touch.
    db.SetEntries.Remove(set);
    await db.SaveChangesAsync(ct);

    return Results.NoContent();
})
   .WithName("DeleteWorkoutSet")
   .WithSummary("Deletes a set")
   .WithDescription("Removes one set from a workout. The remaining sets in the block keep their set numbers.")
   .Produces(StatusCodes.Status204NoContent)
   .Produces(StatusCodes.Status404NotFound);

// Starts Kestrel and blocks until shutdown (Ctrl+C, SIGTERM from the container runtime).
app.Run();

// A local function (it can sit after app.Run() because C# hoists local functions) so
// the protected endpoints share one reading of the "sub" claim. Throwing rather than
// returning 401 is deliberate: by the time a handler runs, OnTokenValidated has already
// confirmed "sub" parses, so a failure here is a bug in the pipeline, not a bad request —
// and a 500 is the right way for a bug to surface rather than being masked as "who are you".
static int ParseUserId(ClaimsPrincipal user)
{
    var subClaim = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (!int.TryParse(subClaim, out var userId))
    {
        throw new InvalidOperationException("Authenticated user has no valid sub claim.");
    }
    return userId;
}

// The one definition of what an ExerciseResponse looks like, shared by GET /exercises and
// PATCH /exercises/{id} so the two can't disagree about SessionCount or LastSet. Takes the
// already-filtered and ordered query and only adds the projection.
//
// "Last" means: the most recent workout containing this exercise (date desc, then
// started_at desc — the same ordering the sessions list uses), then within it the last
// block by position, then the highest set number — with non-warm-up sets ranked ahead of
// warm-ups. So the hint is the final working set of the last session, and only degrades to
// a warm-up when that session logged nothing else for the exercise. A working set from an
// older session is deliberately not preferred over a warm-up from the latest one: the hint
// answers "what did I do last time", not "what is my best".
//
// It walks SetEntry -> WorkoutExercise -> Workout by explicit joins because neither of
// the lower two has a navigation property upward. EF Core folds the correlated subquery
// into the outer SELECT, so a full autocomplete list is still one round trip.
static IQueryable<ExerciseResponse> ProjectExerciseResponses(AppDbContext db, IQueryable<Exercise> exercises) =>
    exercises.Select(e => new ExerciseResponse(
        e.Id,
        e.Name,
        e.IsBodyweight,
        db.WorkoutExercises
            .Where(we => we.ExerciseId == e.Id)
            .Select(we => we.WorkoutId)
            .Distinct()
            .Count(),
        (from se in db.SetEntries
         join we in db.WorkoutExercises on se.WorkoutExerciseId equals we.Id
         join w in db.Workouts on we.WorkoutId equals w.Id
         where we.ExerciseId == e.Id
         orderby w.Date descending, w.StartedAt descending, se.IsWarmup, we.Position descending, se.SetNumber descending
         select new LastSetResponse(se.Weight, se.Reps))
            .FirstOrDefault()));

// A local function to fetch a workout's exercises and their sets in one query. The
// handler for GET /workouts/{id} needs this, and the handler for PATCH /workouts/{id} needs 
// it too because the response includes the exercises even though the PATCH request 
// doesn't change them. The query is a join of three tables (WorkoutExercises, Exercises, SetEntries)
//  and a projection into the response records, so it's easier to keep it in one place than
//  to duplicate the LINQ in two handlers.
static async Task<List<WorkoutExerciseResponse>> GetWorkoutExercisesAsync(AppDbContext db, int workoutId, CancellationToken ct) =>
    await (
        from we in db.WorkoutExercises
        join exercise in db.Exercises on we.ExerciseId equals exercise.Id
        where we.WorkoutId == workoutId
        orderby we.Position
        select new WorkoutExerciseResponse(
            we.Id,
            we.ExerciseId,
            exercise.Name,
            exercise.IsBodyweight,
            we.SetEntries
                .OrderBy(se => se.SetNumber)
                .Select(se => new SetEntryResponse(se.Id, se.SetNumber, se.Reps, se.Weight, se.IsWarmup))
                .ToList()))
        .ToListAsync(ct);

// The first half of "exerciseName is get-or-create, twice over" (PLAN.md, REST API):
// find this user's exercise by its normalized name, or bring one into being. This is the
// hinge the autocomplete design turns on — there is no POST /exercises, so exercises
// exist only because they were used. Shared by the bulk write and POST /sets.
//
// The lookup is by normalized name but the row stores the name as typed: normalization
// is what dedupes "Back Squat" / "back squat" / "  Back  squat ", and PLAN.md is explicit
// that display text keeps the user's own casing and spacing.
//
// Saving here rather than leaving the new row for the caller's SaveChangesAsync is
// deliberate, and the reason both callers open a transaction first: WorkoutExercise
// references its exercise by a bare ExerciseId with no navigation property, so the id has
// to be real before a block can be built around it. The save also makes a name created
// earlier in the same request visible to this lookup, which is what stops one payload
// mentioning a new name twice from inserting two rows and tripping the unique index.
static async Task<Exercise> GetOrCreateExerciseAsync(AppDbContext db, int userId, string name, CancellationToken ct)
{
    var normalizedName = ExerciseNameNormalizer.Normalize(name);

    var exercise = await db.Exercises.SingleOrDefaultAsync(
        e => e.UserId == userId && e.NormalizedName == normalizedName, ct);

    if (exercise is not null)
    {
        return exercise;
    }

    exercise = new Exercise
    {
        UserId = userId,
        Name = name,
        NormalizedName = normalizedName,
    };

    // CreatedAt is left unset so the now() column default fills it in, same as User.
    db.Exercises.Add(exercise);
    await db.SaveChangesAsync(ct);

    return exercise;
}

// The second half of "get-or-create, twice over": the block for an exercise within one
// workout. Only POST /sets uses this — the bulk write always creates fresh blocks, since
// "replace" means the client's array is the whole truth.
//
// FirstOrDefault over the highest position, not SingleOrDefault: one exercise can
// legitimately have two blocks in a session (PLAN.md's "came back to squats later"), and
// Single would throw on exactly the data the schema exists to make representable.
// Appending to the latest block is the right reading of "that exercise's block" for
// someone logging as they go — the earlier block is finished work.
static async Task<WorkoutExercise> GetOrCreateBlockAsync(AppDbContext db, int workoutId, int exerciseId, CancellationToken ct)
{
    var block = await db.WorkoutExercises
        .Where(we => we.WorkoutId == workoutId && we.ExerciseId == exerciseId)
        .OrderByDescending(we => we.Position)
        .FirstOrDefaultAsync(ct);

    if (block is not null)
    {
        return block;
    }

    // Nullable projection so a workout with no blocks yet comes back null instead of
    // throwing; (null ?? -1) + 1 then starts the first block at position 0, which is
    // where the existing seeded data starts too.
    var maxPosition = await db.WorkoutExercises
        .Where(we => we.WorkoutId == workoutId)
        .MaxAsync(we => (int?)we.Position, ct);

    block = new WorkoutExercise
    {
        WorkoutId = workoutId,
        ExerciseId = exerciseId,
        Position = (maxPosition ?? -1) + 1,
    };

    db.WorkoutExercises.Add(block);
    await db.SaveChangesAsync(ct);

    return block;
}

// The second level of the ownership check for the two /sets/{setId} routes, shared
// because PATCH and DELETE need it identically.
//
// The workout-level check has already happened by the time this runs; this answers the
// separate question of whether this particular set is actually *in* that workout, which
// matters because set ids are global — without it, any authenticated caller could edit
// any set in the database by pairing its id with a workout they do own.
//
// It has to be a subquery rather than a dotted path because SetEntry carries only a raw
// WorkoutExerciseId, with no navigation property back up to its block. A miss is a 404
// like every other ownership failure in this codebase, never a 403: "no such set here" is
// the honest answer, and confirming that someone else's set exists is not this API's job.
static async Task<SetEntry?> FindSetInWorkoutAsync(AppDbContext db, int workoutId, int setId, CancellationToken ct) =>
    await db.SetEntries
        .Where(se => se.Id == setId)
        .Where(se => db.WorkoutExercises.Any(we => we.Id == se.WorkoutExerciseId && we.WorkoutId == workoutId))
        .SingleOrDefaultAsync(ct);
