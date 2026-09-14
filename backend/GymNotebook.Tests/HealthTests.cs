using System.Net;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// Smoke tests for the fixture itself as much as for /health: a container that starts, a
// host that boots, a schema that applied. If these fail, nothing else in the suite means much.
public class HealthTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // GetPendingMigrationsAsync compares the migrations compiled into the API assembly
    // with the __EFMigrationsHistory table. Empty means every one of them applied cleanly
    // against an empty database — so they all compile and their SQL is valid Postgres.
    [Fact]
    public async Task Migrations_are_applied()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
