using System.Diagnostics;
using System.Net;
using GymNotebook.Api;
using Xunit.Abstractions;

namespace GymNotebook.Tests.SpikeR4;

// SPIKE A6 (+ the Q5 shared-wait timeout). A request that queued on the shared lock
// behind a deletion must, once the deletion commits, find no account and get 401 — never
// run its handler against a user that no longer exists.
[Collection("SpikeR4")]
public class A6LockCheckTests(SpikeR4Database db, ITestOutputHelper output)
{
    private static Dictionary<string, string?> Guarded(LockCheckVariant variant, int sharedTimeoutMs = 5000) => new()
    {
        ["Spike:R4:Guard"] = "true",
        ["Spike:R4:Variant"] = variant.ToString(),
        ["Spike:R4:SharedLockTimeoutMs"] = sharedTimeoutMs.ToString(),
    };

    [Theory]
    [InlineData(LockCheckVariant.SingleStatement)]
    [InlineData(LockCheckVariant.TwoStatements)]
    [InlineData(LockCheckVariant.Batch)]
    public async Task SharedGuard_WaitedBehindCommittedDeletion_Status(LockCheckVariant variant)
    {
        // Arrange
        await using var host = db.CreateHost(new SpikeHooks(), Guarded(variant));
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, workouts: 2, blocksPerWorkout: 2, setsPerBlock: 2);
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode); // warm-up

        // Act: hold "deletion" open, queue a request behind it, then commit the deletion.
        await using var deletion = await db.HoldExclusiveAsync(userId);
        var pending = client.GetAsync("/auth/me");
        await db.WaitForLockWaitersAsync(userId, 1, TimeSpan.FromSeconds(10));
        await deletion.DeleteUserAndCommitAsync();
        var response = await pending;

        // Assert
        output.WriteLine($"{variant}: {(int)response.StatusCode}");
        if (variant == LockCheckVariant.SingleStatement)
        {
            // The hazard: the statement's snapshot predates the deletion, so the guard passes
            // and the handler then runs against a missing row.
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(LockCheckVariant.SingleStatement)]
    [InlineData(LockCheckVariant.TwoStatements)]
    [InlineData(LockCheckVariant.Batch)]
    public async Task SharedGuard_WaitedBehindCommittedDeletion_OnWorkoutsRead_Status(LockCheckVariant variant)
    {
        // Arrange: the same race on a notebook read, where a stale pass would not crash but
        // silently answer (an empty list) for an account that no longer exists.
        await using var host = db.CreateHost(new SpikeHooks(), Guarded(variant));
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, workouts: 2, blocksPerWorkout: 2, setsPerBlock: 2);
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/workouts")).StatusCode);

        // Act
        await using var deletion = await db.HoldExclusiveAsync(userId);
        var pending = client.GetAsync("/workouts");
        await db.WaitForLockWaitersAsync(userId, 1, TimeSpan.FromSeconds(10));
        await deletion.DeleteUserAndCommitAsync();
        var response = await pending;

        // Assert
        output.WriteLine($"{variant}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        if (variant == LockCheckVariant.SingleStatement)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task SharedGuard_WaitExceedsLockTimeout_Returns503WithRetryAfter()
    {
        // Arrange
        await using var host = db.CreateHost(new SpikeHooks(), Guarded(LockCheckVariant.TwoStatements, sharedTimeoutMs: 500));
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);

        // Act
        await using var deletion = await db.HoldExclusiveAsync(userId);
        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/auth/me");
        stopwatch.Stop();

        // Assert
        output.WriteLine($"503 after {stopwatch.ElapsedMilliseconds} ms: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Contains("temporarily_unavailable", await response.Content.ReadAsStringAsync());
    }
}
