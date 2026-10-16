using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GymNotebook.Tests;

// specs/007 user story 2: POST /account/demo/reset replaces the whole notebook and keeps
// the account, its session and its expiry.
public class DemoResetTests(DemoEnabledGymNotebookFactory factory) : IClassFixture<DemoEnabledGymNotebookFactory>
{
    [Fact]
    public async Task ResetDemo_AfterChanges_ReplacesWholeNotebook()
    {
        // Arrange: a demo with a page deleted, an exercise renamed and a template added.
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;
        var before = await PageIdsAsync(userId);
        await ExecuteAsync(userId,
            "DELETE FROM workouts WHERE id = (SELECT min(id) FROM workouts WHERE user_id = @id)",
            "UPDATE exercises SET name = 'Renamed', normalized_name = 'renamed' WHERE user_id = @id AND name = 'Back Squat'",
            "INSERT INTO templates (user_id, name, normalized_name) VALUES (@id, 'Extra', 'extra')");

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert: a fresh demo — new pages, the seed's names back, the extras gone.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await PageIdsAsync(userId);
        Assert.Empty(after.Intersect(before));
        Assert.InRange(after.Count, 31, 41);
        var exercises = await db.Exercises.Where(e => e.UserId == userId).Select(e => e.Name).ToListAsync();
        Assert.Contains("Back Squat", exercises);
        Assert.DoesNotContain("Renamed", exercises);
        var templates = await db.Templates.Where(t => t.UserId == userId).Select(t => t.Name).ToListAsync();
        Assert.Equal(["Arms & Core", "Lower", "Upper"], templates.Order());
    }

    [Fact]
    public async Task ResetDemo_KeepsSessionTokenAndExpiry()
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        var before = await DemoTestSupport.MeAsync(client);

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert: the same token still works, and the demo ends when it would have.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await DemoTestSupport.MeAsync(client);
        Assert.Equal(before.UserId, after.UserId);
        Assert.Equal(before.DemoExpiresAt, after.DemoExpiresAt);
    }

    [Fact]
    public async Task ResetDemo_RegisteredAccount_Returns403NotADemoAndChangesNothing()
    {
        // Arrange: a registered account with one page.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", EmailTestSupport.SessionToken(user));
        var created = await client.PostAsJsonAsync("/workouts", new { date = "2026-10-01", startedAt = "2026-10-01T17:00:00+03:00" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var before = await PageIdsAsync(user.Id);

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_a_demo", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(before, await PageIdsAsync(user.Id));
    }

    [Theory]
    [InlineData(841)]
    [InlineData(-841)]
    [InlineData(null)]
    public async Task ResetDemo_OffsetOutOfRange_Returns400(int? offset)
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        var before = await PageIdsAsync((await DemoTestSupport.MeAsync(client)).UserId);

        // Act
        var response = await DemoTestSupport.ResetAsync(client, offset);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(before, await PageIdsAsync((await DemoTestSupport.MeAsync(client)).UserId));
    }

    [Fact]
    public async Task ResetDemo_OtherDemoUnaffected()
    {
        // Arrange
        var resetting = factory.CreateClient();
        var other = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(resetting);
        await DemoTestSupport.StartDemoAsync(other);
        var otherId = (await DemoTestSupport.MeAsync(other)).UserId;
        var before = await PageIdsAsync(otherId);

        // Act
        var response = await DemoTestSupport.ResetAsync(resetting);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await PageIdsAsync(otherId));
    }

    private async Task<List<int>> PageIdsAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Workouts.Where(w => w.UserId == userId).OrderBy(w => w.Id).Select(w => w.Id).ToListAsync();
    }

    private async Task ExecuteAsync(int userId, params string[] statements)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var sql in statements)
        {
            await db.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("id", userId));
        }
    }
}

// FR-009/FR-016: the reset's sample details follow the account's consent, and the
// account's privacy answers survive it.
public class DemoPrivacyResetTests(DemoPrivacyEnabledGymNotebookFactory factory) : IClassFixture<DemoPrivacyEnabledGymNotebookFactory>
{
    [Fact]
    public async Task ResetDemo_AfterConsent_IncludesOptionalDetails()
    {
        // Arrange: a demo that went through the notice and allowed optional details.
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        await AcknowledgeAndConsentAsync(client);
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await HasDetailsAsync(userId));
    }

    [Fact]
    public async Task ResetDemo_WithoutConsent_OmitsOptionalDetails()
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await HasDetailsAsync(userId));
    }

    [Fact]
    public async Task ResetDemo_KeepsNoticeAcknowledgementAndConsent()
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        await AcknowledgeAndConsentAsync(client);
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;
        var before = await PrivacyAnswersAsync(userId);

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await PrivacyAnswersAsync(userId));
        Assert.NotNull(before.NoticeVersion);
        Assert.NotNull(before.ConsentVersion);
    }

    private async Task AcknowledgeAndConsentAsync(HttpClient client)
    {
        var notice = factory.Services.GetRequiredService<PrivacyNoticeCatalog>().Current(DateTimeOffset.UtcNow).Version;
        var statement = factory.Services.GetRequiredService<OptionalDetailsConsentCatalog>().Current.Version;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(notice))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/account/privacy/optional-details-consent", new GrantOptionalDetailsConsentRequest(statement))).StatusCode);
    }

    private async Task<bool> HasDetailsAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Workouts.AnyAsync(w => w.UserId == userId &&
            (w.Title != null || w.Location != null || w.Notes != null || w.BodyweightKg != null));
    }

    private async Task<(string? NoticeVersion, DateTimeOffset? NoticeAt, string? ConsentVersion, DateTimeOffset? ConsentAt)> PrivacyAnswersAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.AcknowledgedPrivacyNoticeVersion, user.PrivacyNoticeAcknowledgedAt, user.OptionalDetailsConsentVersion, user.OptionalDetailsConsentedAt);
    }
}

// Research R6: the reset holds exclusive access, so it waits for requests already in
// flight and gives up cleanly when they hold the account too long.
[Collection("Lifecycle")]
public class DemoResetCoordinationTests(TwoHostGymNotebookFixture db)
{
    [Fact]
    public async Task ResetDemo_WhileAnotherRequestHoldsSharedAccess_WaitsThenCompletes()
    {
        // Arrange: a stand-in for an in-flight request, holding shared access.
        await using var host = db.CreateHost(DemoEnabledGymNotebookFactory.HostSettings());
        using var client = host.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var shared = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(@ns, @id)", connection, shared))
        {
            command.Parameters.AddWithValue("ns", AccountLifecycle.LockNamespace);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }

        // Act: the reset queues behind it, then the request finishes.
        var pending = DemoTestSupport.ResetAsync(client);
        await db.WaitForLockWaitersAsync(userId, 1);
        await shared.CommitAsync();
        var response = await pending;

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ResetDemo_ExclusiveWaitTimesOut_Returns503AndChangesNothing()
    {
        // Arrange: the 15 s wait shortened (the write timeout must stay below it), and
        // exclusive access held for the whole request.
        await using var host = db.CreateHost(DemoEnabledGymNotebookFactory.HostSettings(
            ("Lifecycle:ExclusiveLockTimeoutMs", "500"), ("Lifecycle:WriteTimeoutMs", "200")));
        using var client = host.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        HttpResponseMessage response;

        // Act
        await using (await db.HoldExclusiveAsync(userId))
        {
            response = await DemoTestSupport.ResetAsync(client);
        }

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
    }
}
