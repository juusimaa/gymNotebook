using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace GymNotebook.Tests;

public class GymNotebookFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());

        // Program.cs throws at startup if Jwt:Secret is missing. Locally that value comes
        // from user-secrets, which doesn't exist in CI — so it has to be set explicitly
        // here rather than relied on from the environment. Jwt:ExpiryMinutes has a
        // committed default in appsettings.Development.json, but pinning it here too
        // keeps the fixture self-contained instead of depending on which environment
        // WebApplicationFactory happens to boot into.
        builder.UseSetting("Jwt:Secret", "test-signing-key-do-not-use-outside-tests-1234567890");
        builder.UseSetting("Jwt:ExpiryMinutes", "30");

        // Explicit rather than left to whatever's in the test runner's environment, so
        // "registration is open" is a guarantee for every test using this factory, not
        // an accident of what's unset on a given machine.
        builder.UseSetting("INVITE_CODE", "");
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}
