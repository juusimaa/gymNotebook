using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
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

// Register AppDbContext with the Npgsql (PostgreSQL) provider. AddDbContext uses a
// *scoped* lifetime: one AppDbContext per HTTP request, created when a handler asks
// for it and disposed when the response is done. EF Core itself is database-agnostic;
// UseNpgsql is what makes it speak Postgres SQL and types.
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Microsoft.AspNetCore.OpenApi inspects the mapped endpoints and their metadata
// (.WithSummary, .Produces<T> etc. below) and builds the OpenAPI document from them.
// A document transformer is a hook that edits the finished document before it's served.
// Here it only sets the title; in milestone 3 a second one adds the JWT Bearer security
// scheme so protected routes can be called from the Scalar page.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Gym Notebook API";
        document.Info.Description = "Digital replacement for a paper gym log.";
        return Task.CompletedTask;
    });
});

// ---------------------------------------------------------------------------------------
// Phase 2: the app. Build() freezes the service container; from here on we're wiring
// the request pipeline — middleware runs in the order it's added, endpoints last.
// ---------------------------------------------------------------------------------------
var app = builder.Build();

// Dev-only: /openapi/v1.json (the document) and /scalar (the interactive UI that reads
// it). The deployed app has no business publishing its own surface to whoever finds the
// URL. WebApplicationFactory in the tests runs as Development, so the tests see these too.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Template leftover. On the local `http` launch profile there is no HTTPS port to
// redirect to, so this only logs a warning; in Azure the container sits behind TLS
// termination and never sees HTTPS itself. Removed in milestone 2 with the Dockerfile.
app.UseHttpsRedirection();

// The one endpoint so far, and the annotation pattern every later endpoint follows.
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

// Starts Kestrel and blocks until shutdown (Ctrl+C, SIGTERM from the container runtime).
app.Run();
