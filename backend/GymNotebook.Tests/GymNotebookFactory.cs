using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace GymNotebook.Tests;

// The test host. WebApplicationFactory<Program> boots the real app in-process — `Program`
// being the class the compiler generates from Program.cs's top-level statements — with
// Kestrel swapped for an in-memory TestServer. IAsyncLifetime is xUnit's async
// setup/teardown for fixtures: InitializeAsync runs once before the first test in a
// class, DisposeAsync once after the last.
public class GymNotebookFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Exposed so tests that need a valid token without going through the rate-limited
    // /auth/register endpoint (every test in a class shares this one host, and so shares
    // one in-process rate-limit bucket) can seed a User directly and mint its token with
    // JwtTokenFactory instead, using the exact secret and expiry configured below.
    public const string JwtSecret = "test-signing-key-do-not-use-outside-tests-1234567890";
    public const int JwtExpiryMinutes = 30;

    // One throwaway Postgres in Docker per factory instance. The tag must match
    // docker-compose.yml so tests and the local stack run the same server version.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    // Overrides configuration before the host builds. UseSetting writes into the same
    // IConfiguration Program.cs reads, at higher priority than appsettings.json, so the
    // app boots exactly as it would for real apart from these values.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Docker maps the container's port to a random free host port, so the connection
        // string is only known once the container is running — hence a setting computed
        // here rather than a committed appsettings.Test.json.
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());

        // Program.cs throws at startup if Jwt:Secret is missing. Locally that value comes
        // from user-secrets, which doesn't exist in CI — so it has to be set explicitly
        // here rather than relied on from the environment. Jwt:ExpiryMinutes has a
        // committed default in appsettings.Development.json, but pinning it here too
        // keeps the fixture self-contained instead of depending on which environment
        // WebApplicationFactory happens to boot into.
        builder.UseSetting("Jwt:Secret", JwtSecret);
        builder.UseSetting("Jwt:ExpiryMinutes", JwtExpiryMinutes.ToString());

        // Explicit rather than left to whatever's in the test runner's environment, so
        // "registration is open" is a guarantee for every test using this factory, not
        // an accident of what's unset on a given machine.
        builder.UseSetting("INVITE_CODE", "");
    }

    // Start Postgres first: touching `Services` is what lazily builds the host, which runs
    // ConfigureWebHost, which needs the container's mapped port for the connection string.
    // Then migrate, so the schema exists before the first test — the same MigrateAsync
    // entrypoint.sh runs via `--migrate` in the container.
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    // `new` because the base class already has a DisposeAsync (IAsyncDisposable, returning
    // ValueTask) and xUnit's IAsyncLifetime wants one returning Task; C# can't overload on
    // return type alone, so this one hides the base and then calls it explicitly after
    // the container is gone. xUnit calls through the interface, which lands here.
    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}
