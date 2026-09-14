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

// Order matters: authentication reads the bearer token into HttpContext.User, then
// authorization checks that user against each endpoint's requirements. The rate limiter
// is endpoint-aware (RequireRateLimiting is endpoint metadata), so it must come after
// routing — which WebApplication adds implicitly ahead of all three of these.
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
// the handler runs. No AppDbContext needed: everything here comes from the token.
auth.MapGet("/me", (ClaimsPrincipal user) =>
{
    var userId = ParseUserId(user);
    return Results.Ok(new MeResponse(userId));
})
   .RequireAuthorization()
   .WithName("GetCurrentUser")
   .WithSummary("Returns the authenticated user's id")
   .WithDescription("Proves a bearer token is valid and its token_version hasn't been revoked.")
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
