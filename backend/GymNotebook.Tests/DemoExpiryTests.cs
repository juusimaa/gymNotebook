using System.Net;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GymNotebook.Tests;

// specs/007 user story 3: an expired demo stops working at once (research R2), and the
// cleanup deletes it with the same lock and evidence lines as any deletion (research R8).
// On the lifecycle database, for the log capture DeletionLogTests uses.
[Collection("Lifecycle")]
public class DemoExpiryTests(TwoHostGymNotebookFixture db)
{
    [Theory]
    [InlineData("GET", "/auth/me")]
    [InlineData("GET", "/workouts")]
    [InlineData("POST", "/auth/token")]
    public async Task AnyRoute_ExpiredDemoToken_Returns401(string method, string path)
    {
        // Arrange: a working demo, then its expiry passes.
        await using var host = db.CreateHost(DemoEnabledGymNotebookFactory.HostSettings());
        using var client = host.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        await ExpireAsync((await DemoTestSupport.MeAsync(client)).UserId);

        // Act
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        // Assert: the same bare 401 as a revoked token, renewal included.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteExpiredDemo_Expired_RemovesAccountAndNotebookAndLogsEvidence()
    {
        // Arrange
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(DemoEnabledGymNotebookFactory.HostSettings(),
            services: s => s.AddSingleton<ILoggerProvider>(logs));
        var userId = await StartDemoAsync(host);
        var rows = await DeletionTestSupport.RowIdsAsync(db, userId);
        Assert.NotEmpty(rows.SetIds);
        await ExpireAsync(userId);

        // Act
        bool deleted;
        using (var scope = host.Services.CreateScope())
        {
            deleted = await scope.ServiceProvider.GetRequiredService<AccountDeletion>().DeleteExpiredDemoAsync(userId, CancellationToken.None);
        }

        // Assert: every row gone, templates included, and the intent and committed lines
        // for this account's privacy id.
        Assert.True(deleted);
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, rows));
        await using (var context = db.NewContext())
        {
            Assert.False(await context.Templates.AnyAsync(t => t.UserId == userId));
        }
        var events = logs.DeletionEvents()
            .Where(e => e.State.Any(p => p.Key == "PrivacyAccountId"))
            .Select(e => e.State.Single(p => p.Key == "Event").Value as string)
            .ToList();
        Assert.Equal(["deletion.intent", "deletion.committed"], events);
    }

    [Fact]
    public async Task DeleteExpiredDemo_NotExpiredOrRegistered_DeletesNothing()
    {
        // Arrange: a live demo and a registered account.
        await using var host = db.CreateHost(DemoEnabledGymNotebookFactory.HostSettings());
        var liveDemo = await StartDemoAsync(host);
        var registered = await db.SeedUserAsync();
        var liveBefore = await DeletionTestSupport.SnapshotAccountAsync(db, liveDemo);
        var registeredBefore = await DeletionTestSupport.SnapshotAccountAsync(db, registered);

        // Act
        bool deletedLive, deletedRegistered;
        using (var scope = host.Services.CreateScope())
        {
            var deletion = scope.ServiceProvider.GetRequiredService<AccountDeletion>();
            deletedLive = await deletion.DeleteExpiredDemoAsync(liveDemo, CancellationToken.None);
            deletedRegistered = await deletion.DeleteExpiredDemoAsync(registered, CancellationToken.None);
        }

        // Assert
        Assert.False(deletedLive);
        Assert.False(deletedRegistered);
        Assert.Equal(liveBefore, await DeletionTestSupport.SnapshotAccountAsync(db, liveDemo));
        Assert.Equal(registeredBefore, await DeletionTestSupport.SnapshotAccountAsync(db, registered));
    }

    [Fact]
    public async Task CleanupRunOnce_DeletesOnlyExpiredDemos()
    {
        // Arrange: an expired demo, a live demo and a registered account.
        await using var host = db.CreateHost(DemoEnabledGymNotebookFactory.HostSettings());
        var expired = await StartDemoAsync(host);
        var live = await StartDemoAsync(host);
        var registered = await db.SeedUserAsync();
        await ExpireAsync(expired);
        var worker = host.Services.GetServices<IHostedService>().OfType<DemoCleanupWorker>().Single();

        // Act
        var deleted = await worker.RunOnceAsync(CancellationToken.None);

        // Assert: other tests' expired demos may go too, so only these three are checked.
        Assert.True(deleted >= 1);
        await using var context = db.NewContext();
        var remaining = await context.Users.Where(u => u.Id == expired || u.Id == live || u.Id == registered).Select(u => u.Id).ToListAsync();
        Assert.Equal(new[] { live, registered }.Order(), remaining.Order());
    }

    private static async Task<int> StartDemoAsync(LifecycleTestHost host)
    {
        using var client = host.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);
        return (await DemoTestSupport.MeAsync(client)).UserId;
    }

    private async Task ExpireAsync(int userId)
    {
        await using var context = db.NewContext();
        await context.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DemoExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
}
