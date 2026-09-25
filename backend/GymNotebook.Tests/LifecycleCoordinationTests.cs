using System.Diagnostics;
using System.Net;

namespace GymNotebook.Tests;

// How guarded reads coordinate with account deletion (research R4 → Q3/Q5, spike A3/A6).
// A test stands in for the deletion by holding exclusive access on its own connection;
// requests are known to be queued behind it when pg_locks shows them waiting.
[Collection("Lifecycle")]
public class LifecycleCoordinationTests(TwoHostGymNotebookFixture db)
{
    [Theory]
    [InlineData("/auth/me")]
    [InlineData("/workouts")]
    public async Task GuardedRead_WaitedBehindCommittedDeletion_Returns401(string path)
    {
        // Arrange: two hosts, so the "deleted" account's request lands on a different app
        // instance than the one that proved the token works.
        await using var warmHost = db.CreateHost();
        await using var readHost = db.CreateHost();
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, workouts: 2, blocksPerWorkout: 2, setsPerBlock: 2);
        using var warmClient = warmHost.ClientFor(userId);
        using var readClient = readHost.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await warmClient.GetAsync(path)).StatusCode);

        // Act: hold "deletion" open, queue a read behind it, then commit the deletion.
        await using var deletion = await db.HoldExclusiveAsync(userId);
        var pending = readClient.GetAsync(path);
        await db.WaitForLockWaitersAsync(userId, 1);
        await deletion.DeleteUserAndCommitAsync();
        var response = await pending;

        // Assert: the fresh check under the lock finds no account. Not a 500 from a
        // handler running against a missing row, and not a 200 answering for it (spike A6).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GuardedRead_SharedLockWaitExceedsTimeout_Returns503WithRetryAfter()
    {
        // Arrange: Q4's 5 s wait shortened so the test doesn't sit through it; the code
        // path is the same.
        await using var host = db.CreateHost(new Dictionary<string, string?> { ["Lifecycle:SharedLockTimeoutMs"] = "500" });
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);

        // Act: exclusive access held and never released during the request.
        await using var deletion = await db.HoldExclusiveAsync(userId);
        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/auth/me");
        stopwatch.Stop();

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Contains("\"code\":\"temporarily_unavailable\"", await response.Content.ReadAsStringAsync());
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(450), $"gave up after {stopwatch.ElapsedMilliseconds} ms, before the lock wait");
    }

    [Fact]
    public async Task GuardedRead_ClientStopsReading_HoldsSharedAccessUntilWriteTimeout()
    {
        // Arrange: real Kestrel (TestServer has no socket buffers to fill) and a workout
        // whose JSON is several MB, so the filter's response write really blocks while it
        // still holds shared access. Kestrel's own stalled-response detection is off, so
        // the only thing that can end the write is the filter's write timeout (spike A3).
        await using var host = db.CreateHost(
            new Dictionary<string, string?> { ["Lifecycle:WriteTimeoutMs"] = "1500" },
            kestrel: o =>
            {
                o.Listen(IPAddress.Loopback, 0);
                o.Limits.MinResponseDataRate = null;
            });
        var userId = await db.SeedUserAsync();
        var workoutId = await db.SeedHugeWorkoutAsync(userId, blocks: 40, setsPerBlock: 2500);
        using var client = host.ClientFor(userId);

        // Act: read the first 64 KB, then stop reading without closing the connection.
        using var response = await client.GetAsync($"/workouts/{workoutId}", HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        await body.ReadAtLeastAsync(buffer, buffer.Length);
        var stalled = Stopwatch.StartNew();
        var heldWhileStalled = await db.CountLifecycleLocksAsync(userId, granted: true);
        await TwoHostGymNotebookFixture.WaitForAsync(
            async () => await db.CountLifecycleLocksAsync(userId, granted: true) == 0,
            TimeSpan.FromSeconds(10), "the stalled read to release shared access");
        stalled.Stop();

        // Assert: shared access was held through the write, and released at about the
        // write timeout — the bound that keeps a stalled client from blocking deletion.
        Assert.Equal(1, heldWhileStalled);
        Assert.True(stalled.Elapsed < TimeSpan.FromMilliseconds(1500 + 3000), $"released after {stalled.ElapsedMilliseconds} ms");
    }
}
