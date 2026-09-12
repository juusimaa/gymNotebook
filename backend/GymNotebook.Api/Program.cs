using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Gym Notebook API";
        document.Info.Description = "Digital replacement for a paper gym log.";
        return Task.CompletedTask;
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new HealthResponse("ok", "reachable"))
        : Results.Json(new HealthResponse("degraded", "unreachable"), statusCode: StatusCodes.Status503ServiceUnavailable))
   .WithName("GetHealth")
   .WithTags("Health")
   .WithSummary("Liveness and database reachability")
   .WithDescription("Returns 200 when the API is up and the database answers, 503 when the database is unreachable. Used by Compose and Azure probes.")
   .Produces<HealthResponse>()
   .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);


app.Run();
