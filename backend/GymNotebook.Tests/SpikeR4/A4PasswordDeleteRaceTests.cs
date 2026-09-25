using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using Xunit.Abstractions;

namespace GymNotebook.Tests.SpikeR4;

// SPIKE A4: password change racing account deletion across two hosts. Pass: no stale
// token is ever emitted, no deadlock, and exclusive access is never held while the
// shared guard is requested.
[Collection("SpikeR4")]
public class A4PasswordDeleteRaceTests(SpikeR4Database db, ITestOutputHelper output)
{
    private static readonly object _changeBody = new { currentPassword = SpikeR4Database.Password, newPassword = "a-new-password" };

    [Fact]
    public async Task ChangePasswordThenDelete_DeleteWithOldToken_Returns401()
    {
        // Arrange
        var hooks = new SpikeHooks();
        await using var hostA = db.CreateHost(hooks);
        await using var hostB = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        using var clientA = hostA.ClientFor(userId);
        using var clientB = hostB.ClientFor(userId);

        // Act
        var change = await clientA.PostAsJsonAsync("/spike/auth/change-password", _changeBody);
        var delete = await clientB.PostAsync("/spike/account/delete", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.True(await db.UserExistsAsync(userId));
    }

    [Fact]
    public async Task DeleteThenChangePassword_Returns401()
    {
        // Arrange
        var hooks = new SpikeHooks();
        await using var hostA = db.CreateHost(hooks);
        await using var hostB = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        using var clientA = hostA.ClientFor(userId);
        using var clientB = hostB.ClientFor(userId);

        // Act
        var delete = await clientB.PostAsync("/spike/account/delete", null);
        var change = await clientA.PostAsJsonAsync("/spike/auth/change-password", _changeBody);

        // Assert
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }

    [Fact]
    public async Task DeletionCommitsBetweenPasswordCommitAndDelivery_NoTokenEmitted()
    {
        // Arrange: pause the password change after its exclusive commit, before delivery.
        var hooks = new SpikeHooks();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.On("pw.committed", async _ =>
        {
            committed.SetResult();
            await release.Task;
        });
        await using var hostA = db.CreateHost(hooks);
        await using var hostB = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        using var clientA = hostA.ClientFor(userId);
        // Another session that signed in after the change, so it holds the new version (1).
        using var clientB = hostB.ClientFor(userId, tokenVersion: 1);

        // Act
        var change = clientA.PostAsJsonAsync("/spike/auth/change-password", _changeBody);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopwatch = Stopwatch.StartNew();
        var delete = await clientB.PostAsync("/spike/account/delete", null);
        stopwatch.Stop();
        release.SetResult();
        var changeResponse = await change.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: deletion was not blocked by the paused change (it holds no lock there),
        // and the change's delivery guard suppressed the token.
        output.WriteLine($"delete took {stopwatch.ElapsedMilliseconds} ms; change → {(int)changeResponse.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, changeResponse.StatusCode);
        Assert.DoesNotContain("token", await changeResponse.Content.ReadAsStringAsync());
        Assert.Contains(hooks.Events, e => e.Name == "pw.suppressed");
    }

    [Fact]
    public async Task ConcurrentChangeAndDelete_ManyRounds_NeverBothSucceedNoDeadlockNo5xx()
    {
        // Arrange
        var hooks = new SpikeHooks();
        await using var hostA = db.CreateHost(hooks);
        await using var hostB = db.CreateHost(hooks);
        var outcomes = new Dictionary<string, int>();
        const int Rounds = 30;

        for (var round = 0; round < Rounds; round++)
        {
            var userId = await db.SeedUserAsync();
            using var clientA = hostA.ClientFor(userId);
            using var clientB = hostB.ClientFor(userId);

            // Act: start both as close together as possible.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var change = Task.Run(async () => { await start.Task; return await clientA.PostAsJsonAsync("/spike/auth/change-password", _changeBody); });
            // The change runs BCrypt (~100+ ms) before it locks, so an unstaggered delete
            // always wins; a random stagger spreads rounds across both orderings. This is
            // distribution, not synchronization — correctness never depends on it.
            var stagger = Random.Shared.Next(0, 400);
            var delete = Task.Run(async () => { await start.Task; await Task.Delay(stagger); return await clientB.PostAsync("/spike/account/delete", null); });
            start.SetResult();
            var responses = await Task.WhenAll(change, delete).WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            var changeStatus = responses[0].StatusCode;
            var deleteStatus = responses[1].StatusCode;
            var key = $"change {(int)changeStatus} / delete {(int)deleteStatus}";
            outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
            Assert.False(changeStatus == HttpStatusCode.OK && deleteStatus == HttpStatusCode.OK, "both succeeded");
            Assert.Contains(changeStatus, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
            Assert.Contains(deleteStatus, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
            Assert.Equal(deleteStatus != HttpStatusCode.OK, await db.UserExistsAsync(userId));
        }

        foreach (var (key, count) in outcomes)
        {
            output.WriteLine($"{key}: {count}");
        }
    }
}
