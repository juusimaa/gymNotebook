using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
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
// Present isn't enough: an unparseable value makes Npgsql throw an exception that quotes
// it, password and all, into the logs. Fail here instead, without the value.
ConnectionStringValidation.EnsureValid(connectionString);

// Jwt:Secret signs every token, so it's as sensitive as the database password and
// lives in the same places (user-secrets locally, a Container Apps secret in Azure).
// Jwt:ExpiryMinutes isn't sensitive and has a committed default in
// appsettings.Development.json, so no `?? throw` for it.
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");
var jwtExpiryMinutes = builder.Configuration.GetValue<int>("Jwt:ExpiryMinutes");
var jwtRenewalCapHours = builder.Configuration.GetValue("Jwt:RenewalCapHours", 12);
if (jwtRenewalCapHours <= 0)
{
    throw new InvalidOperationException("Jwt:RenewalCapHours must be positive.");
}
// A BCrypt hash of a random string nobody knows, computed once at boot. The auth routes
// verify a password against it when the address has no account, so "no such account"
// takes as long as "wrong password" — the slow BCrypt check is otherwise the one
// measurable difference between the two (specs/002 FR-014). It can never match anything.
var dummyPasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

// Production switch for the privacy and account lifecycle feature (specs/001, plan.md
// P25). Main deploys automatically, so unfinished privacy routes must be able to merge
// without going live. A missing, empty or misspelled value fails closed (feature off),
// and only the exact string "true" turns it on — "True", "1" or "yes" do not, so a typo
// can never enable it by accident. The privacy routes (PrivacyEndpoints.cs) are mapped
// only when it's true.
var privacyLifecycleEnabled = builder.Configuration["PRIVACY_LIFECYCLE_ENABLED"] == "true";

// Whether to take the client address from X-Forwarded-For (see UseForwardedHeaders below).
// Only right when a trusted proxy is the sole way in — Container Apps ingress in Azure,
// where the Bicep sets it. Locally nothing sits in front of the API, so trusting the header
// would let any caller choose their own rate-limit bucket. Same exact-"true" rule as the
// privacy switch: missing or misspelled leaves it off, which keeps today's behavior.
var forwardedHeadersEnabled = builder.Configuration["FORWARDED_HEADERS_ENABLED"] == "true";

// Comma-separated origins the browser may call this API from — the Vite dev server now, the
// deployed frontend URL later. An origin is scheme + host + port with no trailing slash
// (http://localhost:5173/ silently matches nothing), and it's localhost even inside Compose:
// the value is compared against what the browser sends, so Host=db-style service names
// don't apply. Empty is never a valid state, and Compose turns a missing
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

// The two email policies (specs/002 plan D12, FR-020), per client IP like "auth" but per
// hour: "email-request" on the routes that send an email (resend, reset request),
// "email-link" on the routes that consume a link. Configurable for the same reason
// as above — the test host raises them so a class of tests isn't throttled by its own
// earlier tests, and EmailRateLimitedGymNotebookFactory shrinks them to hit them.
var emailRequestPermitLimit = builder.Configuration.GetValue("EmailRequestRateLimit:PermitLimit", 5);
var emailRequestWindowSeconds = builder.Configuration.GetValue("EmailRequestRateLimit:WindowSeconds", 3600);
var emailLinkPermitLimit = builder.Configuration.GetValue("EmailLinkRateLimit:PermitLimit", 10);
var emailLinkWindowSeconds = builder.Configuration.GetValue("EmailLinkRateLimit:WindowSeconds", 3600);

// The per-account bucket for password-verified account operations (specs/001 P12): 10
// attempts per 60 s per account. Same override pattern, so tests can shrink or widen it.
var sensitivePermitLimit = builder.Configuration.GetValue("SensitiveRateLimit:PermitLimit", 10);
var sensitiveWindowSeconds = builder.Configuration.GetValue("SensitiveRateLimit:WindowSeconds", 60);
var restorePermitLimit = builder.Configuration.GetValue("RestoreRateLimit:PermitLimit", 5);
var restoreWindowSeconds = builder.Configuration.GetValue("RestoreRateLimit:WindowSeconds", 600);

// Lock and write timeouts for account-lifecycle coordination (see AccountLifecycle.cs).
// The class's defaults are what production runs with; tests shorten them. Validated at
// boot because a wrong ordering (write timeout ≥ exclusive wait) fails only under load.
var lifecycleOptions = builder.Configuration.GetSection("Lifecycle").Get<LifecycleOptions>() ?? new LifecycleOptions();
lifecycleOptions.Validate();
builder.Services.AddSingleton(lifecycleOptions);
var restoreOptions = builder.Configuration.GetSection("Restore").Get<RestoreOptions>() ?? new RestoreOptions();
restoreOptions.Validate();
builder.Services.AddSingleton(restoreOptions);

// The versioned privacy notices embedded from docs/privacy/notices/ (PrivacyNoticeCatalog).
// Loaded even while the feature flag is off, so a broken or missing notice file fails
// this deploy at boot instead of surfacing only on the day the flag is switched on.
builder.Services.AddSingleton(PrivacyNoticeCatalog.LoadEmbedded());

// The optional-details consent statement from docs/privacy/consent/ (specs/001 user story
// 6), loaded at boot for the same reason: a broken file fails the deploy, not the switch-on.
builder.Services.AddSingleton(OptionalDetailsConsentCatalog.LoadEmbedded());

// The clock the privacy endpoints read: which notice is current depends on the time (an
// announced successor takes effect at its effectiveAt), and deletion deadlines count from
// it. Injected rather than calling DateTimeOffset.UtcNow so tests can control time
// deterministically.
builder.Services.AddSingleton(TimeProvider.System);

// The notebook export (NotebookExport.cs) and account deletion (AccountDeletion.cs),
// scoped like the AppDbContext they work through.
builder.Services.AddScoped<NotebookExport>();
builder.Services.AddScoped<NotebookRestore>();
builder.Services.AddScoped<AccountDeletion>();

// Outgoing email (specs/002 plan D6–D8). Loaded and checked here, at boot, like the
// connection string: EmailOptions.Load throws if the backend is unknown, if console/memory
// is configured in Production, or if resend is missing its key, sender or app URL.
var emailOptions = EmailOptions.Load(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton<EmailTemplates>();

// The queue handlers write to, and the background worker that drains it: applies the caps,
// then hands each message to the IEmailSender registered below.
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddHostedService<EmailOutboxWorker>();

// The caps are keyed with Jwt:Secret (see EmailCaps.HashRecipient), so they need a factory
// to receive it; everything else comes from the container as usual.
builder.Services.AddScoped(services => new EmailCaps(
    services.GetRequiredService<AppDbContext>(),
    jwtSecret,
    emailOptions.DailyCap,
    services.GetRequiredService<TimeProvider>()));

// The Turnstile bot check on signup and reset requests (specs/002 plan D9). Loaded here so
// a secret without hostnames stops the boot (TurnstileOptions.Load). Always registered,
// on or off: the handlers ask it either way, and it answers "pass" when it's off. Same
// typed-HttpClient pattern as the Resend sender below; the 10-second timeout bounds how
// long a signup waits on Cloudflare before failing closed.
builder.Services.AddSingleton(TurnstileOptions.Load(builder.Configuration));
builder.Services.AddHttpClient<Turnstile>(client =>
{
    client.BaseAddress = new Uri("https://challenges.cloudflare.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Exactly one sender, chosen by Email:Backend.
switch (emailOptions.Backend)
{
    case EmailOptions.ConsoleBackend:
        builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();
        break;
    case EmailOptions.MemoryBackend:
        // Registered under its own type as well, so tests can resolve MemoryEmailSender
        // and read Messages; both registrations return the same singleton.
        builder.Services.AddSingleton<MemoryEmailSender>();
        builder.Services.AddSingleton<IEmailSender>(services => services.GetRequiredService<MemoryEmailSender>());
        break;
    case EmailOptions.ResendBackend:
        // A typed HttpClient (IHttpClientFactory, part of ASP.NET Core): the factory pools
        // and recycles the underlying handlers, which avoids both socket exhaustion from
        // new HttpClient() per call and stale DNS from one client kept forever. The
        // 10-second timeout bounds how long one stuck send holds up the queue behind it.
        builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        break;
}

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
                // A link token (confirmation or reset, see JwtTokenFactory) is signed with
                // the same secret, so it passes the signature check above. Its "purpose"
                // claim is what marks it as not-a-session (specs/002 FR-006): a reset
                // token even carries a valid "tv", so without this a link from an email
                // would work as a bearer token for its whole lifetime.
                if (context.Principal?.FindFirst(JwtTokenFactory.PurposeClaim) is not null)
                {
                    context.Fail("Link tokens are not sessions.");
                    return;
                }

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
                // Guarded routes re-check this under the lifecycle lock (AccountLifecycle.cs);
                // this earlier check stays so a stale token is rejected before any
                // transaction or lock is taken.
                if (user is null || user.TokenVersion.ToString() != tvClaim)
                {
                    context.Fail("Token has been revoked.");
                    return;
                }

                // A backstop for suspended accounts (specs/001 research R6 Q2c), at no extra
                // query since the row is already loaded. Login already refuses them, and
                // tokens from before a restore die with the signing-key rotation; this
                // covers anything that slips past both.
                if (user.SignInSuspendedAt is not null)
                {
                    if (context.Request.Path.Equals("/auth/token", StringComparison.OrdinalIgnoreCase))
                    {
                        context.HttpContext.Items["renewal_account_suspended"] = true;
                    }
                    context.Fail("Account sign-in is suspended.");
                    return;
                }

                // An unconfirmed account can't sign in (login answers 403
                // email_not_verified), so no code path mints it a session. This check
                // makes sure no token would work for one anyway (specs/002 plan D5,
                // FR-004); the lifecycle guard repeats it under the lock.
                if (user.EmailVerifiedAt is null)
                {
                    context.Fail("Email address is not confirmed.");
                }
            },
            OnChallenge = async context =>
            {
                // Other bearer routes retain their existing bare 401. Renewal has an
                // explicit suspended-account response in its API contract.
                if (context.HttpContext.Items.ContainsKey("renewal_account_suspended"))
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsJsonAsync(new ErrorResponse("account_suspended"));
                }
            },
        };
    });

// The services behind .RequireAuthorization(). No named policies: "any authenticated
// user" is the only rule this API has.
builder.Services.AddAuthorization();

// Named per-IP policies, applied only to the endpoints that opt in with
// .RequireRateLimiting(name): "auth" on login and register (see PLAN.md, Rate limiting),
// and the two email policies on the email routes.
builder.Services.AddRateLimiter(options =>
{
    // The default rejection status is 503, which reads as "server broken"; 429 tells the
    // caller the problem is them.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A fixed window per client IP (the forwarded one when FORWARDED_HEADERS_ENABLED is
    // on, see UseForwardedHeaders): each address gets PermitLimit requests per Window,
    // then is rejected until the window resets. QueueLimit = 0 refuses excess requests
    // immediately instead of parking them until a permit frees up. Counters live in
    // process — correct with one replica, and the thing that needs shared state if this
    // ever scales out.
    options.AddPolicy("auth", httpContext => PerClientIp(httpContext, rateLimitPermitLimit, rateLimitWindowSeconds));

    // The email policies (specs/002 plan D12): same per-IP shape, hourly windows. Each
    // named policy keeps its own counters, so a burst of sign-ins doesn't spend the
    // resend budget or the other way round.
    options.AddPolicy("email-request", httpContext => PerClientIp(httpContext, emailRequestPermitLimit, emailRequestWindowSeconds));
    options.AddPolicy("email-link", httpContext => PerClientIp(httpContext, emailLinkPermitLimit, emailLinkWindowSeconds));
    options.AddPolicy("restore", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = restorePermitLimit,
            Window = TimeSpan.FromSeconds(restoreWindowSeconds),
            QueueLimit = 0,
        }));

    // The per-account limit on password-verified account operations (specs/001 P12,
    // research R10), for endpoints marked with SensitiveOperationMetadata. It has to be the
    // global limiter rather than a second named policy: the middleware applies only one
    // named policy per endpoint, and these endpoints keep "auth" as well. Every other
    // endpoint gets the no-op limiter. The key is the validated "sub" claim — never anything
    // the client sends — and it's always there: authorization has already turned away
    // unauthenticated requests by the time this middleware runs.
    //
    // Like "auth", the counters live in process, so the real bound is this limit times the
    // number of running instances: one normally, two during a revision overlap. An exact
    // aggregate bound with more replicas would need a shared limiter.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        httpContext.GetEndpoint()?.Metadata.GetMetadata<SensitiveOperationMetadata>() is null
            ? RateLimitPartition.GetNoLimiter("")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = sensitivePermitLimit,
                    Window = TimeSpan.FromSeconds(sensitiveWindowSeconds),
                    QueueLimit = 0,
                }));

    // Tell a rejected caller when the window resets (contracts/api.md: 429 "with
    // Retry-After when available"). Fixed-window leases always carry it.
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
        return ValueTask.CompletedTask;
    };
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

// The real client address, ahead of everything that reads it (the "auth" rate limiter).
// Behind Container Apps ingress, Connection.RemoteIpAddress is the ingress itself, so
// without this every visitor shares one login/register bucket (specs/001 research R10).
// The ingress appends the caller's address as the *rightmost* X-Forwarded-For entry;
// anything to its left came from the client and can be forged. ForwardLimit = 1 takes
// only that rightmost entry. The known-proxy lists are cleared because the ingress's
// internal address isn't ours to pin (no custom VNet) — the default list is loopback only,
// which would silently ignore the header. That's safe only because external ingress is the
// sole route into the container, hence the switch. X-Forwarded-Proto isn't processed:
// nothing here builds URLs or redirects by scheme.
if (forwardedHeadersEnabled)
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        ForwardLimit = 1,
    };
    forwardedOptions.KnownIPNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);
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
// An endpoint filter runs too late for 401/429 and JSON-binding errors. Backup's
// contract requires no-store on those responses too, so use its endpoint metadata
// after routing but before any of these early exits. Other routes are unaffected.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<BackupNoStoreMetadata>() is not null)
    {
        // OnStarting also survives an error handler clearing response headers.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }
    await next(context);
});
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

// Every route mapped on `auth` gets the /auth prefix. The group is also where metadata
// every auth endpoint shares goes: none of their answers — tokens, the address on /me,
// and the errors — may be kept by the browser or a cache in between (specs/002
// contracts/api.md → Common behavior).
var auth = app.MapGroup("/auth").AddEndpointFilter(PrivacyEndpoints.NoStore);

// Signup (specs/002 Story 1, contracts/api.md → Register). The answer is 202 with no body
// for *every* valid request, whether the address is new, already has a confirmed account,
// or has an unconfirmed one: a different answer would let anyone find out which addresses
// have accounts by trying them (FR-011). What differs goes only to that address's inbox:
//
//   no account   create it, unconfirmed       → confirmation link
//   confirmed    change nothing               → "you already have an account", no link
//   unconfirmed  change nothing, discard the  → "finish creating your account" with a
//                submitted password              *reset* link, so the inbox's owner — not
//                                                whoever signed up first — picks the password
//
// No token comes back: the account can't sign in until the address is confirmed.
auth.MapPost("/register", async (RegisterRequest request, AppDbContext db, EmailOutbox outbox, EmailTemplates templates, Turnstile turnstile, TimeProvider clock, HttpContext http, CancellationToken ct) =>
{
    var email = AccountInput.NormalizeEmail(request.Email);
    var displayName = AccountInput.NormalizeDisplayName(request.DisplayName);

    // A malformed field says nothing about accounts, so 400 here is safe.
    if (!AccountInput.IsValidEmail(email) || displayName is null || !AccountInput.IsValidPassword(request.Password))
    {
        return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    }

    // The bot check (FR-019), when it's configured. After the field checks, so a malformed
    // form doesn't spend a call to Cloudflare; before the address is looked up and before
    // the password is hashed, so a refused request reveals nothing about accounts and costs
    // the server almost nothing.
    if (!await turnstile.VerifyAsync(request.TurnstileToken, http.Connection.RemoteIpAddress?.ToString(), Turnstile.SignupAction, ct))
    {
        return Results.Json(new ErrorResponse("captcha"), statusCode: StatusCodes.Status400BadRequest);
    }

    // Hashed on every branch, before the lookup, even though only "no account" stores it.
    // BCrypt is slow on purpose (~100 ms); skipping it on the other two branches would make
    // "this address has an account" measurably faster to answer (FR-014). The email itself
    // is sent by the outbox worker after this response, so its latency can't leak either.
    var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
    var now = clock.GetUtcNow();

    var existing = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);
    if (existing is null)
    {
        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHash,
            TokenVersion = 0,
            // Generated here, never accepted from the client (specs/001 data-model.md). A
            // random (v4) UUID rather than a time-ordered v7 on purpose: this id appears in
            // deletion log lines, and it shouldn't reveal when the account was created.
            PrivacyAccountId = Guid.NewGuid(),
        };

        // CreatedAt is deliberately left unset: EF Core recognizes the CLR default value on
        // a DateTimeOffset property and omits the column from the INSERT, letting the
        // "now()" column default configured in AppDbContext fill it in. EmailVerifiedAt
        // stays null: unconfirmed until the link is used.
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            outbox.Enqueue(templates.Confirmation(email, JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Verify, jwtSecret, now)));
            return Results.Accepted();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ix_users_email"))
        {
            // Two signups for this address at the same instant: both looked, both found
            // nothing, and the other one's INSERT won the unique index. From here on this
            // is simply the "existing account" case (specs/002 plan D4, Edge Cases).
            db.ChangeTracker.Clear();
            existing = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, ct);
        }
    }

    outbox.Enqueue(existing.EmailVerifiedAt is null
        ? templates.FinishSignup(email, JwtTokenFactory.CreateLinkToken(existing, LinkPurpose.Reset, jwtSecret, now))
        : templates.AlreadyRegistered(email));
    return Results.Accepted();
}).WithName("RegisterUser")
   .WithSummary("Registers a new user")
   .WithDescription("Starts creating an account for the given email address. Always answers 202 with no body for valid input, whether or not the address already has an account; what differs is only in the email sent to that address. The account can sign in once the address is confirmed. When the Turnstile bot check is configured, a missing or failing turnstileToken is 400 with code \"captcha\".")
   .Produces(StatusCodes.Status202Accepted)
   .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
   .RequireRateLimiting("auth");

auth.MapPost("/login", async (LoginRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
{
    // A blank field can never sign anyone in, and says nothing about accounts.
    var email = AccountInput.NormalizeEmail(request.Email);
    if (email.Length == 0 || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    }

    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);

    // Same 401 whether the address has no account or the password is wrong — a different
    // response for each would let a caller enumerate accounts by trying addresses one at a
    // time and watching which error comes back. The BCrypt check runs either way (against
    // the dummy hash when there's no account), so the timing doesn't tell them apart
    // either. A password over BCrypt's 72 bytes can't belong to any account (FR-005), but
    // is still checked for the same reason.
    var passwordMatches = BCrypt.Net.BCrypt.Verify(request.Password, user?.PasswordHash ?? dummyPasswordHash)
        && AccountInput.IsValidPassword(request.Password);
    if (user is null || !passwordMatches)
    {
        return Results.Unauthorized();
    }

    // Checked only *after* the password verified (specs/001 research R4 → Q5), so the
    // suspension is revealed only to someone who already knows the password; a wrong
    // password still gets the generic 401 above. The code lets the UI show the privacy
    // contact path. Login takes no lifecycle lock (Q6): suspension is only ever set while
    // ingress is disabled, and a token issued in a race is rejected on first use anyway.
    // Before the confirmation check, so a suspended account says so whether or not its
    // address was confirmed (specs/002 Story 2, scenario 3).
    if (user.SignInSuspendedAt is not null)
    {
        return Results.Json(new ErrorResponse("account_suspended"), statusCode: StatusCodes.Status403Forbidden);
    }

    // Right password, unconfirmed address (specs/002 FR-003). Also only after the
    // password, for the same reason as suspension. The UI turns this into "check your
    // inbox" with a button that calls POST /auth/verification.
    if (user.EmailVerifiedAt is null)
    {
        return Results.Json(new ErrorResponse("email_not_verified"), statusCode: StatusCodes.Status403Forbidden);
    }

    var token = JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes, clock.GetUtcNow());
    return Results.Ok(new AuthResponse(token));
}).WithName("LoginUser")
   .WithSummary("Logs in a user")
   .WithDescription("Authenticates a user with the given email address and password. Returns a JWT token for authentication. After a correct password only: 403 account_suspended for a suspended account, otherwise 403 email_not_verified for an unconfirmed address.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status401Unauthorized)
   .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
   .RequireRateLimiting("auth");

// The bearer middleware has checked signature, token version and account state. Its
// clock skew can accept a just-expired token, so this route checks expiry exactly.
// A token from before auth_time was introduced stays valid but cannot be renewed.
auth.MapPost("/token", (ClaimsPrincipal caller, TimeProvider clock) =>
{
    var now = clock.GetUtcNow();
    var expiry = caller.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
    if (!long.TryParse(expiry, out var expirySeconds) || expirySeconds <= now.ToUnixTimeSeconds())
    {
        return Results.Unauthorized();
    }

    var raw = caller.FindFirst(JwtTokenFactory.AuthTimeClaim)?.Value;
    if (!long.TryParse(raw, out var seconds))
    {
        return Results.Json(new ErrorResponse("renewal_refused"), statusCode: StatusCodes.Status403Forbidden);
    }

    DateTimeOffset authTime;
    try
    {
        authTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.Json(new ErrorResponse("renewal_refused"), statusCode: StatusCodes.Status403Forbidden);
    }

    if (authTime > now || now - authTime > TimeSpan.FromHours(jwtRenewalCapHours))
    {
        return Results.Json(new ErrorResponse("renewal_refused"), statusCode: StatusCodes.Status403Forbidden);
    }

    // These claims passed OnTokenValidated's current-account check above.
    var userId = int.Parse(caller.FindFirst(JwtRegisteredClaimNames.Sub)!.Value, CultureInfo.InvariantCulture);
    var tokenVersion = int.Parse(caller.FindFirst("tv")!.Value, CultureInfo.InvariantCulture);
    return Results.Ok(new AuthResponse(JwtTokenFactory.CreateToken(userId, tokenVersion, jwtSecret, jwtExpiryMinutes, now, authTime)));
})
   .RequireAuthorization()
   .RequireAccountLifecycle()
   .WithName("RenewToken")
   .WithTags("Auth")
   .WithSummary("Renews a valid session token")
   .WithDescription("Returns a fresh bearer token with the original sign-in time. Refuses tokens without auth_time or sessions past the renewal cap.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status401Unauthorized)
   .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
   .Produces(StatusCodes.Status429TooManyRequests)
   .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
   .RequireRateLimiting("auth");

// Send the confirmation link again (specs/002 FR-012), from the "check your inbox" screen.
// Always 204: whether an email went out depends on the account, and the answer must not.
// It sends only when the password is right and the address unconfirmed — requiring the
// password means this can't be used to make the app email arbitrary addresses, and the
// email caps (EmailCaps) bound how often even the owner can.
auth.MapPost("/verification", async (ResendVerificationRequest request, AppDbContext db, EmailOutbox outbox, EmailTemplates templates, TimeProvider clock, CancellationToken ct) =>
{
    var email = AccountInput.NormalizeEmail(request.Email);
    if (email.Length == 0 || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    }

    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);

    // The dummy hash again, so "no account" isn't the fast branch.
    var passwordMatches = BCrypt.Net.BCrypt.Verify(request.Password, user?.PasswordHash ?? dummyPasswordHash)
        && AccountInput.IsValidPassword(request.Password);
    if (user is not null && passwordMatches && user.EmailVerifiedAt is null)
    {
        outbox.Enqueue(templates.Confirmation(email, JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Verify, jwtSecret, clock.GetUtcNow())));
    }

    return Results.NoContent();
}).WithName("ResendVerification")
   .WithSummary("Sends the email confirmation link again")
   .WithDescription("Always answers 204. Sends a new confirmation link only if the password is correct and the address is not confirmed yet.")
   .Produces(StatusCodes.Status204NoContent)
   .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
   .RequireRateLimiting("email-request");

// The confirmation link's target (specs/002 Story 1, scenario 3). The page at
// /verify-email reads the token from the URL fragment and posts it here — a POST from
// script rather than a GET on the link itself, so a mail scanner that fetches links
// without running script confirms nothing (spec Edge Cases).
//
// Idempotent: opening the link twice says "confirmed" both times. No session is needed or
// created; the answer carries the address so the sign-in screen can pre-fill it. Link
// failures are 400 expired/invalid, never 401, because the frontend treats every 401 as
// "your session ended" (FR-009).
auth.MapPost("/verify-email", async (VerifyEmailRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
{
    var now = clock.GetUtcNow();
    var link = JwtTokenFactory.ReadLinkToken(request.Token, LinkPurpose.Verify, jwtSecret, now);
    if (link.Status != LinkTokenStatus.Valid || link.Email is null)
    {
        return LinkError(link.Status);
    }

    // Stamps the time only the first time (the `EmailVerifiedAt == null` filter), as one
    // UPDATE, so two clicks at once can't race. Matching the address as well as the id
    // means the token confirms exactly the address it was minted for: if that account was
    // deleted and its id ever reused (a restore rewinds the sequence), nothing happens.
    await db.Users
        .Where(u => u.Id == link.UserId && u.Email == link.Email && u.EmailVerifiedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, now), ct);

    // Zero rows updated means either "already confirmed" (fine, idempotent) or "no such
    // account any more" (the link is dead). One indexed lookup tells them apart.
    var exists = await db.Users.AnyAsync(u => u.Id == link.UserId && u.Email == link.Email, ct);
    return exists
        ? Results.Ok(new VerifyEmailResponse(link.Email))
        : LinkError(LinkTokenStatus.Invalid);
}).WithName("VerifyEmail")
   .WithSummary("Confirms an email address from its link")
   .WithDescription("Takes the token from a confirmation link. Returns the confirmed address, also when it was already confirmed. 400 with code expired or invalid when the link doesn't work.")
   .Produces<VerifyEmailResponse>(StatusCodes.Status200OK)
   .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
   .RequireRateLimiting("email-link");

// "Forgot your password?" (specs/002 Story 3, FR-013). Always 202: whether a link went
// out depends on the account, and the answer must not. Any existing account gets one,
// confirmed or not — completing a reset confirms the address (FR-008), so someone who
// forgot their password before confirming isn't locked out.
//
// No branch hashes a password here, so the "account exists" branch does the same work as
// the other one (one indexed lookup) and needs no dummy hash; the email itself is sent by
// the outbox worker after this response. The email caps (EmailCaps) bound how often one
// inbox can be sent a link, whoever asks.
auth.MapPost("/password-reset", async (PasswordResetRequest request, AppDbContext db, EmailOutbox outbox, EmailTemplates templates, Turnstile turnstile, TimeProvider clock, HttpContext http, CancellationToken ct) =>
{
    // A malformed address says nothing about accounts, so 400 here is safe.
    var email = AccountInput.NormalizeEmail(request.Email);
    if (!AccountInput.IsValidEmail(email))
    {
        return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    }

    // The bot check, as on /register and placed the same way: before the lookup, so a
    // refusal is the same whether or not the address has an account.
    if (!await turnstile.VerifyAsync(request.TurnstileToken, http.Connection.RemoteIpAddress?.ToString(), Turnstile.PasswordResetAction, ct))
    {
        return Results.Json(new ErrorResponse("captcha"), statusCode: StatusCodes.Status400BadRequest);
    }

    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);
    if (user is not null)
    {
        outbox.Enqueue(templates.PasswordReset(email, JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset, jwtSecret, clock.GetUtcNow())));
    }

    return Results.Accepted();
}).WithName("RequestPasswordReset")
   .WithSummary("Emails a password reset link")
   .WithDescription("Always answers 202 for a well-formed address. Sends a reset link, valid for one hour, only if the address has an account. When the Turnstile bot check is configured, a missing or failing turnstileToken is 400 with code \"captcha\".")
   .Produces(StatusCodes.Status202Accepted)
   .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
   .RequireRateLimiting("email-request");

// The reset link's target (specs/002 Story 3, FR-007, FR-008). The page at
// /reset-password reads the token from the URL fragment and posts it here with the new
// password. Succeeding signs this browser in — the answer is a fresh session token — and
// every other session out.
//
// A link works once without any token table: it carries the account's TokenVersion
// ("tv"), and completing the reset bumps it, so the same link then reads as stale. Every
// way a link can fail is 400 expired/invalid, never 401, because the frontend treats a
// 401 as "your session ended" (FR-009).
auth.MapPost("/password-reset/confirm", async (ConfirmPasswordResetRequest request, AppDbContext db, LifecycleOptions lifecycle, TimeProvider clock, HttpContext http, CancellationToken ct) =>
{
    var now = clock.GetUtcNow();
    var link = JwtTokenFactory.ReadLinkToken(request.Token, LinkPurpose.Reset, jwtSecret, now);
    if (link.Status != LinkTokenStatus.Valid || link.Email is null)
    {
        return LinkError(link.Status);
    }

    // After the link, so a dead link is reported as such even if the form was also wrong:
    // asking for a new link is what the user needs to hear first.
    if (!AccountInput.IsValidPassword(request.NewPassword))
    {
        return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    }

    // A first, unlocked look: is the link already used, or is the account gone? The
    // version is checked again under the lock by PasswordReplacement, which is what makes
    // single use race-free; this look saves hashing a password for a link that can't work.
    //
    // Matching the address as well as the id, as /verify-email does, means the link only
    // ever works for the account at the inbox it was sent to: if that account was
    // deleted and its id reused (a restore rewinds the sequence), the new account's
    // version could match, but its address won't.
    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == link.UserId && u.Email == link.Email, ct);
    if (user is null || user.TokenVersion != link.TokenVersion)
    {
        return LinkError(LinkTokenStatus.Invalid);
    }

    // A suspended account changes nothing (Story 3, scenario 4) and gets the same 403 as
    // login, so the screen can show the privacy contact path. Only someone holding a live
    // link for the account learns this. No lock needed for the same reason as login
    // (specs/001 Q6): suspension is only ever set while ingress is disabled.
    if (user.SignInSuspendedAt is not null)
    {
        return Results.Json(new ErrorResponse("account_suspended"), statusCode: StatusCodes.Status403Forbidden);
    }

    // The link's "tv" is the version the lock check expects, so two tabs submitting the
    // same link at once can't both win: the second finds the version already bumped.
    var newHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
    var outcome = await PasswordReplacement.ReplaceAsync(db, user.Id, link.TokenVersion, newHash, confirmEmailAt: now, lifecycle, ct);
    switch (outcome)
    {
        case PasswordReplacementOutcome.Busy:
            return AccountLifecycle.TemporarilyUnavailable(http);
        // Revoked: someone else used the link, or changed the password, first. Superseded:
        // this reset committed, but a deletion or another change followed before a token
        // could go out. Either way this link has nothing more to give — and there is no
        // session to end, so not 401.
        case PasswordReplacementOutcome.Revoked or PasswordReplacementOutcome.Superseded:
            return LinkError(LinkTokenStatus.Invalid);
    }

    user.TokenVersion = link.TokenVersion + 1;
    var token = JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes, clock.GetUtcNow());
    return Results.Ok(new AuthResponse(token));
}).WithName("ConfirmPasswordReset")
   .WithSummary("Sets a new password from a reset link")
   .WithDescription("Takes the token from a reset link and the new password. Sets it, signs out every other session, confirms the address if it wasn't yet, and returns a fresh JWT. 400 with code expired or invalid when the link doesn't work (also once it has been used), invalid_request for a password that breaks the rules; 403 account_suspended; 503 temporarily_unavailable if the account is busy with another lifecycle operation.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
   .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
   .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
   .RequireRateLimiting("email-link");

// The smallest possible protected route. ClaimsPrincipal is another parameter Minimal
// APIs knows how to supply: it's HttpContext.User, already populated by the bearer
// handler — and already past the token_version check in OnTokenValidated — by the time
// the handler runs.
//
// The name and address come from the row, not the token: the JWT carries only "sub" (the
// id) and "tv", and adding more claims would mean a token outliving a change to them. One
// indexed lookup by primary key is cheap, and the cover page needs both.
auth.MapGet("/me", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(user);

    // SingleAsync, not SingleOrDefaultAsync: the lifecycle filter has just confirmed, under
    // its lock, that this row exists — and a deletion can't commit while the lock is held —
    // so a miss here is a bug, not a 404. Same reasoning as ParseUserId.
    var me = await db.Users
        .Where(u => u.Id == userId)
        .Select(u => new MeResponse(u.Id, u.DisplayName, u.Email))
        .SingleAsync(ct);

    return Results.Ok(me);
})
   .RequireAuthorization()
   .RequireAccountLifecycle()
   .WithName("GetCurrentUser")
   .WithSummary("Returns the authenticated user's id, display name and email address")
   .WithDescription("Proves a bearer token is valid and its token_version hasn't been revoked. The display name and email address are what the frontend shows on the cover page.")
   .Produces<MeResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status401Unauthorized);

// Requires a valid token *and* the current password: a stolen token alone shouldn't be
// enough to change the password and lock the real owner out.
//
// Revoking every token is an account-lifecycle operation, so the write goes through
// PasswordReplacement, which takes *exclusive* access (specs/001 research R4) — and so
// this endpoint is not under the shared-access LifecycleFilter (see there for why).
auth.MapPost("/change-password", async (ChangePasswordRequest request, ClaimsPrincipal caller, AppDbContext db, LifecycleOptions lifecycle, TimeProvider clock, HttpContext http, CancellationToken ct) =>
{
    // The new password follows the same rules as at signup (AccountInput, specs/002
    // FR-005), including BCrypt's 72-byte bound.
    if (string.IsNullOrWhiteSpace(request.CurrentPassword) || !AccountInput.IsValidPassword(request.NewPassword))
    {
        return Results.BadRequest();
    }

    var (userId, tokenVersion) = AccountLifecycle.ReadClaims(caller);

    // AsNoTracking: OnTokenValidated's FindAsync already tracks this User, and a tracked
    // query would hand back that instance — loaded before any lock, so possibly stale.
    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);

    // Same 401 as login for a wrong current password. The `user is null` branch covers an
    // account deleted since OnTokenValidated looked.
    if (user is null || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    // Hashed before PasswordReplacement takes the exclusive lock (see there for why).
    var newHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
    var outcome = await PasswordReplacement.ReplaceAsync(db, userId, tokenVersion, newHash, confirmEmailAt: null, lifecycle, ct);
    switch (outcome)
    {
        case PasswordReplacementOutcome.Busy:
            return AccountLifecycle.TemporarilyUnavailable(http);
        // Revoked: this token was already stale under the lock — the same bare 401 as any
        // revoked token. Superseded: the change committed, but a deletion or another
        // change followed, so the new token would be stale too (spike A4).
        case PasswordReplacementOutcome.Revoked or PasswordReplacementOutcome.Superseded:
            return Results.Unauthorized();
    }

    // `user` is an untracked copy, so updating it changes nothing in the database; it only
    // lets the token factory mint a token with the version that was just committed.
    user.TokenVersion = tokenVersion + 1;
    var token = JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes, clock.GetUtcNow());
    return Results.Ok(new AuthResponse(token));
})
   .RequireAuthorization()
   .WithName("ChangePassword")
   .WithSummary("Changes the authenticated user's password")
   .WithDescription("Updates the authenticated user's password after verifying the current password. Returns 503 temporarily_unavailable if the account is busy with another lifecycle operation.")
   .Produces<AuthResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status401Unauthorized)
   .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

// Every /exercises and /workouts route runs under the account-lifecycle filter (shared
// access plus a fresh account check, see AccountLifecycle.cs). Applied to the whole group
// so a route added later can't forget it; LifecycleCoverageTests enforces the same.
var exercises = app.MapGroup("/exercises").RequireAuthorization().RequireAccountLifecycle();

// excludeWorkoutId is for the edit page of a session still in progress: "last time" must
// mean the session before this one, not a set this very page has already logged. It only
// narrows which sets the hints read, so an id that isn't the caller's simply excludes
// nothing of theirs — there is no ownership check to make and nothing to 404.
exercises.MapGet("/", async (string? search, int? excludeWorkoutId, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);
    var query = db.Exercises.Where(e => e.UserId == userId);

    if (!string.IsNullOrWhiteSpace(search))
    {
        var normalizedSearch = ExerciseNameNormalizer.Normalize(search);
        query = query.Where(e => e.NormalizedName.Contains(normalizedSearch));
    }

    var results = await ProjectExerciseResponses(db, query.OrderBy(e => e.Name), excludeWorkoutId).ToListAsync(ct);

    return Results.Ok(results);
})
   .WithName("SearchExercises")
   .WithSummary("Searches the caller's exercises")
   .WithDescription("Exercise-index and autocomplete lookup scoped to the authenticated user. Returns every exercise when search is omitted. Each result carries its distinct session count, its last set (the latest session's last non-warm-up set, or its last warm-up set if that session had nothing else) and its first set (that same session's first set, warm-up or not); both are null if the exercise has never been logged. excludeWorkoutId leaves one workout out of both sets, so a page being edited doesn't see its own sets as \"last time\".")
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
    var response = await ProjectExerciseResponses(db, db.Exercises.Where(e => e.Id == exercise.Id), excludeWorkoutId: null).SingleAsync(ct);
    return Results.Ok(response);
})
   .WithName("UpdateExercise")
   .WithSummary("Updates an existing exercise")
   .WithDescription("Modifies the name and/or bodyweight status of an existing exercise. Only the owner can update their exercises.")
   .Produces<ExerciseResponse>(StatusCodes.Status200OK)
   .Produces(StatusCodes.Status404NotFound);

var workouts = app.MapGroup("/workouts").RequireAuthorization().RequireAccountLifecycle();

workouts.MapPost("/", async (CreateWorkoutRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
{
    var userId = ParseUserId(caller);

    // Optional-details consent (specs/001 user story 6, FR-032), checked before anything
    // is stored. With PRIVACY_LIFECYCLE_ENABLED off this is always Store: production keeps
    // accepting the details without consent until the feature is switched on — the
    // owner-accepted interim risk (spec Clarifications, FR-035).
    var decision = await OptionalDetails.DecideAsync(db, userId, privacyLifecycleEnabled,
        request.Title, request.BodyweightKg, request.Location, request.Notes, ct);
    if (decision == OptionalDetailsDecision.Reject)
    {
        return OptionalDetails.ConsentRequired();
    }
    var storeAsNull = decision == OptionalDetailsDecision.StoreAsNull;

    var workout = new Workout
    {
        UserId = userId,
        Date = request.Date,
        // PostgreSQL timestamptz stores an instant rather than its original offset,
        // and Npgsql requires DateTimeOffset values to have offset zero. Accept any
        // valid offset at the HTTP boundary, then normalize it before persistence.
        StartedAt = request.StartedAt.ToUniversalTime(),
        Title = storeAsNull ? null : request.Title,
        BodyweightKg = request.BodyweightKg,
        Location = storeAsNull ? null : request.Location,
        Notes = storeAsNull ? null : request.Notes,
    };

    db.Workouts.Add(workout);
    await db.SaveChangesAsync(ct);

    var response = new WorkoutDetailResponse(workout.Id, workout.Date, workout.StartedAt, workout.EndedAt,
        workout.Title, workout.BodyweightKg, workout.Location, workout.Notes, Exercises: []);

    return Results.Created($"/workouts/{workout.Id}", response);
})
   .WithName("CreateWorkout")
   .WithSummary("Creates a new workout")
   .WithDescription("Starts a new session page for the authenticated user. With the privacy feature on, a non-empty title, location, notes or bodyweight without optional-details consent gets 403 optional_details_consent_required and nothing is stored.")
   .Produces<WorkoutDetailResponse>(StatusCodes.Status201Created)
   .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

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

    // Optional-details consent, as in POST, and like it before any change: only the
    // fields this request actually sets are passed, so omitting a detail never needs
    // consent. The flag-off behaviour is the same owner-accepted interim as in POST.
    var decision = await OptionalDetails.DecideAsync(db, userId, privacyLifecycleEnabled,
        request.HasTitle ? request.Title : null,
        request.HasBodyweightKg ? request.BodyweightKg : null,
        request.HasLocation ? request.Location : null,
        request.HasNotes ? request.Notes : null, ct);
    if (decision == OptionalDetailsDecision.Reject)
    {
        return OptionalDetails.ConsentRequired();
    }
    var storeAsNull = decision == OptionalDetailsDecision.StoreAsNull;

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
        workout.Title = storeAsNull ? null : request.Title;
    }

    if (request.HasBodyweightKg)
    {
        workout.BodyweightKg = request.BodyweightKg;
    }

    if (request.HasLocation)
    {
        workout.Location = storeAsNull ? null : request.Location;
    }

    if (request.HasNotes)
    {
        workout.Notes = storeAsNull ? null : request.Notes;
    }

    await db.SaveChangesAsync(ct);

    var workoutExercises = await GetWorkoutExercisesAsync(db, workout.Id, ct);

    var response = new WorkoutDetailResponse(workout.Id, workout.Date, workout.StartedAt, workout.EndedAt,
        workout.Title, workout.BodyweightKg, workout.Location, workout.Notes, workoutExercises);
    return Results.Ok(response);
})
    .WithName("UpdateWorkout")
    .WithSummary("Updates an existing workout")
    .WithDescription("Modifies only the supplied workout fields. Explicit null clears nullable fields; date and startedAt cannot be null. Only the owner can update their workouts. With the privacy feature on, setting a non-empty title, location, notes or bodyweight without optional-details consent gets 403 optional_details_consent_required and nothing is changed.")
    .Produces<WorkoutDetailResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
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

    // This handler genuinely needs several saves: WorkoutExercise has no Exercise
    // *navigation property* (just the raw ExerciseId), so EF Core has no way to fix up the
    // foreign key of a block whose exercise was created in this same request — the new
    // Exercise has to reach the database and get its id before the block can point at it.
    // Saving as we go also lets each new exercise be visible to the next block's lookup,
    // which is what makes the same new name appearing twice in one payload resolve to one
    // Exercise row instead of two.
    //
    // PLAN.md's rule still holds — the bulk write rolls back whole, never half-applies —
    // because every save below runs inside the transaction the lifecycle filter opened for
    // this request (AccountLifecycle.cs). The filter commits only when this handler returns
    // a 2xx, so the 400 in the loop below, or any exception, rolls back the saves before
    // it. The handler used to open its own transaction; EF can't nest one inside the
    // filter's, so it joins that one instead.

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
        if (!WorkoutInputValidation.HasExerciseName(input.ExerciseName))
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
    if (!WorkoutInputValidation.HasExerciseName(request.ExerciseName))
    {
        return Results.BadRequest();
    }

    // Same reasoning as the PUT above: the exercise and the block each need to exist in
    // the database before the next step can reference them by id, and a failure partway
    // through should not leave an empty block behind. The lifecycle filter's transaction
    // provides that: it commits only if this handler returns its 201.

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
// specs/001: the public notice and the account's acknowledgement state (user story 1), the
// account deletion (user story 4) and optional-details consent (user story 6).
// Not mapped at all while the feature flag is off, so the routes 404 (plan.md P25).
// specs/004: export and backup status are independent of the privacy rollout.
app.MapBackupEndpoints(restoreOptions, privacyLifecycleEnabled);
if (privacyLifecycleEnabled)
{
    app.MapPrivacyEndpoints();
}

app.Run();

// A local function (it can sit after app.Run() because C# hoists local functions) so
// the protected endpoints share one reading of the "sub" claim. Throwing rather than
// returning 401 is deliberate: by the time a handler runs, OnTokenValidated has already
// confirmed "sub" parses, so a failure here is a bug in the pipeline, not a bad request —
// and a 500 is the right way for a bug to surface rather than being masked as "who are you".
// 400 {"code":"expired"|"invalid"} for a link token that didn't read as Valid (specs/002
// contracts/api.md: never 401, see FR-009).
static IResult LinkError(LinkTokenStatus status) => Results.Json(
    new ErrorResponse(status == LinkTokenStatus.Expired ? "expired" : "invalid"),
    statusCode: StatusCodes.Status400BadRequest);

// True when a save failed on the named unique index — Postgres's error 23505, which EF
// wraps in a DbUpdateException. Matching the constraint name, not just the code, so an
// unrelated unique violation is never mistaken for "this address is taken".
static bool IsUniqueViolation(DbUpdateException ex, string constraintName) =>
    ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
    && pg.ConstraintName == constraintName;

// The fixed-window, per-client-IP limiter behind every named rate-limit policy ("auth",
// "email-request", "email-link"); see the comment on the "auth" policy for why each part
// is the way it is.
static RateLimitPartition<string> PerClientIp(HttpContext httpContext, int permitLimit, int windowSeconds) =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
        });

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
// a warm-up when that session logged nothing else for the exercise — and then says so
// through IsWarmup, which the UI prints next to the figure. A working set from an
// older session is deliberately not preferred over a warm-up from the latest one: the hint
// answers "what did I do last time", not "what is my best".
//
// FirstSet is the other end of that same session: its first block by position, then the
// lowest set number, warm-up or not. The editor starts a new block from it when it is a
// warm-up, so a session that opened with a warm-up last time opens with one again. Both
// subqueries break a tie between two workouts with the same date and start time by the
// higher id, so they can never describe two different sessions.
//
// excludeWorkoutId (null for none) leaves one workout out of both, which is how the edit
// page asks for the session before itself. SessionCount still counts every page.
//
// It walks SetEntry -> WorkoutExercise -> Workout by explicit joins because neither of
// the lower two has a navigation property upward. EF Core folds the correlated subqueries
// into the outer SELECT, so a full autocomplete list is still one round trip.
static IQueryable<ExerciseResponse> ProjectExerciseResponses(AppDbContext db, IQueryable<Exercise> exercises, int? excludeWorkoutId) =>
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
         where we.ExerciseId == e.Id && (excludeWorkoutId == null || w.Id != excludeWorkoutId)
         orderby w.Date descending, w.StartedAt descending, w.Id descending, se.IsWarmup, we.Position descending, se.SetNumber descending
         select new LastSetResponse(se.Weight, se.Reps, se.IsWarmup))
            .FirstOrDefault(),
        (from se in db.SetEntries
         join we in db.WorkoutExercises on se.WorkoutExerciseId equals we.Id
         join w in db.Workouts on we.WorkoutId equals w.Id
         where we.ExerciseId == e.Id && (excludeWorkoutId == null || w.Id != excludeWorkoutId)
         orderby w.Date descending, w.StartedAt descending, w.Id descending, we.Position, se.SetNumber
         select new LastSetResponse(se.Weight, se.Reps, se.IsWarmup))
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
// deliberate, and why both callers rely on the lifecycle filter's transaction to keep
// their several saves all-or-nothing: WorkoutExercise
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
