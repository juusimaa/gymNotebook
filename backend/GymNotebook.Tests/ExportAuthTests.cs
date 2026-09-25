using System.Net;
using System.Net.Http.Json;
using System.Text;
using GymNotebook.Api;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GymNotebook.Tests;

// specs/001 user story 3 (tasks.md T045): who may take an export, and how often. Each test
// builds its own host, so each starts with fresh in-process rate-limit counters.
[Collection("Lifecycle")]
public class ExportAuthTests(TwoHostGymNotebookFixture db)
{
    [Fact]
    public async Task Export_WrongPassword_Returns400PasswordVerificationFailedWithNoFile()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client, "not-the-password");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_verification_failed", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Export_EmptyPassword_Returns400InvalidRequest()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client, "");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Export_PasswordWithSurroundingSpaces_IsNotTrimmed()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client, $" {TwoHostGymNotebookFixture.Password} ");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_RevokedToken_Returns401()
    {
        // Arrange: the token carries version 0; a password change moved the account to 1.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        await db.ExecuteAsync("UPDATE users SET token_version = 1 WHERE id = @id", userId);
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_ExpiredToken_Returns401()
    {
        // Arrange: expired ten minutes ago, beyond the bearer handler's 5-minute clock skew.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.CreateClient();
        var expired = JwtTokenFactory.CreateToken(
            new User { Id = userId, Username = "", PasswordHash = "", PrivacyAccountId = Guid.Empty },
            GymNotebookFactory.JwtSecret, -10);
        client.DefaultRequestHeaders.Authorization = new("Bearer", expired);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_MoreThanTenAttemptsIn60Seconds_Returns429ForThatAccountOnly()
    {
        // Arrange: the per-IP limit widened, so only the per-account limit (10/60 s) is in play.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(("RateLimit:PermitLimit", "100")));
        var userId = await db.SeedUserAsync();
        var otherUserId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        using var otherClient = host.ClientFor(otherUserId);

        // Act
        var attempts = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            attempts.Add((await ExportTestSupport.ExportAsync(client, "wrong")).StatusCode);
        }
        var eleventh = await ExportTestSupport.ExportAsync(client, "wrong");
        var otherAccount = await ExportTestSupport.ExportAsync(otherClient, "wrong");

        // Assert
        Assert.All(attempts, status => Assert.Equal(HttpStatusCode.BadRequest, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        Assert.True(eleventh.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.BadRequest, otherAccount.StatusCode);
    }

    [Fact]
    public async Task Export_ExportLockAlreadyHeld_Returns429ExportInProgress()
    {
        // Arrange: another export's snapshot, standing in for one on another instance.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, @id)", connection, transaction))
        {
            command.Parameters.AddWithValue("ns", NotebookExport.ExportLockNamespace);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("export_in_progress", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task Export_SecondExportFromAnotherHost_Returns429AndFirstCompletes()
    {
        // Arrange: host A's export paused between table queries, holding its export lock.
        var barrier = new QueryBarrier("FROM workouts AS");
        await using var hostA = db.CreateHost(ExportTestSupport.FlagOn(),
            services: s => s.ConfigureDbContext<Api.Data.AppDbContext>(o => o.AddInterceptors(barrier)));
        await using var hostB = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 2, 1, 1);
        using var clientA = hostA.ClientFor(userId);
        using var clientB = hostB.ClientFor(userId);

        // Act
        var first = ExportTestSupport.ExportAsync(clientA);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await ExportTestSupport.ExportAsync(clientB);
        barrier.Release();
        var firstResponse = await first;

        // Assert: the rejected export disclosed nothing; the first one finished normally.
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("export_in_progress", (await second.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(0, await db.CountExportLocksAsync(userId));
    }

    [Fact]
    public async Task Export_NonJsonBody_Returns415()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await client.PostAsync("/account/export",
            new StringContent($"currentPassword={TwoHostGymNotebookFixture.Password}", Encoding.UTF8, "application/x-www-form-urlencoded"));

        // Assert
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Export_MalformedJson_Returns400()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await client.PostAsync("/account/export",
            new StringContent("{\"currentPassword\":", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_FlagOff_Returns404()
    {
        // Arrange: the host's default, PRIVACY_LIFECYCLE_ENABLED=false.
        await using var host = db.CreateHost();
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
