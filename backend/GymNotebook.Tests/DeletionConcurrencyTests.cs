using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymNotebook.Tests;

// specs/001 user story 4 (tasks.md T058, quickstart §4): a real deletion on one app host
// racing everything else the account can do on another, in both orders. Where the Phase 2
// lifecycle tests stood in for deletion with a raw SQL holder, these run the endpoint
// itself, so they prove AccountDeletion takes exclusive access at the right point.
//
// Orderings are forced with barriers, never sleeps:
//
//   - Other operation first: a QueryBarrier pauses its handler at its first notebook
//     command — after the lifecycle filter has taken shared access — so the deletion
//     demonstrably queues behind it (pg_locks shows the waiter) before it's released.
//   - Deletion first: a QueryBarrier pauses the deletion at its first DELETE, when it
//     holds exclusive access, so the other operation demonstrably queues behind it.
[Collection("Lifecycle")]
public class DeletionConcurrencyTests(TwoHostGymNotebookFixture db)
{
    // The first command a notebook handler sends that isn't OnTokenValidated's user lookup
    // (which runs before the filter, so pausing there would prove nothing).
    private static readonly Func<string, bool> _firstHandlerCommand = sql => !sql.Contains("FROM users AS", StringComparison.Ordinal);

    private const string DeletionHoldsExclusive = "DELETE FROM workouts";

    // What the other request is. Write: an earlier write may commit before the deletion
    // (and is then erased) and its answer may be 2xx or, if the deletion wins its delivery
    // guard, 401. Read: an earlier read writes its response under shared access, so it
    // always completes with 200.
    private enum Kind { Write, Read }

    private sealed record Scenario(Kind Kind, Func<HttpClient, SeededNotebook, Task<HttpResponseMessage>> Send, Func<string, bool>? Pause = null);

    private sealed record SeededNotebook(int WorkoutId, int SetId, int FirstExerciseId, int SecondExerciseId);

    private static readonly Dictionary<string, Scenario> _scenarios = new()
    {
        ["create workout"] = new(Kind.Write, (c, _) => c.PostAsJsonAsync("/workouts", new { date = "2026-09-01", startedAt = "2026-09-01T08:00:00Z" })),
        ["patch workout"] = new(Kind.Write, (c, n) => c.PatchAsJsonAsync($"/workouts/{n.WorkoutId}", new { endedAt = "2026-01-01T09:00:00Z" })),
        ["delete workout"] = new(Kind.Write, (c, n) => c.DeleteAsync($"/workouts/{n.WorkoutId}")),
        ["replace blocks"] = new(Kind.Write, (c, n) => c.PutAsJsonAsync($"/workouts/{n.WorkoutId}/exercises",
            new { exercises = new[] { new { exerciseName = "Exercise 0", sets = new[] { new { weight = 50m, reps = 5, isWarmup = false } } } } })),
        ["append set"] = new(Kind.Write, (c, n) => c.PostAsJsonAsync($"/workouts/{n.WorkoutId}/sets", new { exerciseName = "Exercise 0", weight = 60m, reps = 3, isWarmup = false })),
        ["update set"] = new(Kind.Write, (c, n) => c.PatchAsJsonAsync($"/workouts/{n.WorkoutId}/sets/{n.SetId}", new { weight = 70m, reps = 2, isWarmup = false })),
        ["delete set"] = new(Kind.Write, (c, n) => c.DeleteAsync($"/workouts/{n.WorkoutId}/sets/{n.SetId}")),
        ["rename exercise"] = new(Kind.Write, (c, n) => c.PatchAsJsonAsync($"/exercises/{n.FirstExerciseId}", new { name = "Renamed during deletion" })),
        // Renaming onto another exercise's name merges the two (PLAN.md).
        ["merge exercise"] = new(Kind.Write, (c, n) => c.PatchAsJsonAsync($"/exercises/{n.SecondExerciseId}", new { name = "Exercise 0" })),
        ["acknowledge notice"] = new(Kind.Write, AcknowledgeAsync),
        ["me"] = new(Kind.Read, (c, _) => c.GetAsync("/auth/me"), Pause: sql => sql.Contains("SELECT u.username", StringComparison.Ordinal)),
        ["read workouts"] = new(Kind.Read, (c, _) => c.GetAsync("/workouts")),
        ["export"] = new(Kind.Read, (c, _) => ExportTestSupport.ExportAsync(c)),
    };

    // The export doesn't hold account access while it streams, so "export first" has its
    // own test below (Delete_ExportStreaming_...), not this ordering.
    public static TheoryData<string> OtherOperationFirstScenarios() => [.. _scenarios.Keys.Where(k => k != "export")];

    public static TheoryData<string> DeletionFirstScenarios() => [.. _scenarios.Keys];

    [Theory]
    [MemberData(nameof(OtherOperationFirstScenarios))]
    public async Task Delete_OtherOperationHoldsSharedAccess_WaitsThenErasesEverything(string scenarioName)
    {
        // Arrange
        var scenario = _scenarios[scenarioName];
        var barrier = new QueryBarrier(scenario.Pause ?? _firstHandlerCommand);
        await using var otherHost = CreateHost(barrier);
        await using var deletionHost = CreateHost();
        var (userId, notebook, rows) = await SeedAccountAsync();
        var (controlId, _, _) = await SeedAccountAsync();
        var controlBefore = await DeletionTestSupport.SnapshotAccountAsync(db, controlId);
        using var otherClient = otherHost.ClientFor(userId);
        using var deletionClient = deletionHost.ClientFor(userId);

        // Act: the operation is inside its shared critical section when the deletion arrives.
        var other = scenario.Send(otherClient, notebook);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var heldShared = await db.CountLifecycleLocksAsync(userId, granted: true);
        var deletion = DeletionTestSupport.DeleteAsync(deletionClient);
        await db.WaitForLockWaitersAsync(userId, 1);
        barrier.Release();
        var otherResponse = await other;
        var deletionResponse = await deletion;

        // Assert
        Assert.Equal(1, heldShared);
        if (scenario.Kind == Kind.Read)
        {
            Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        }
        else
        {
            Assert.True(otherResponse.IsSuccessStatusCode || otherResponse.StatusCode == HttpStatusCode.Unauthorized,
                $"{scenarioName} answered {otherResponse.StatusCode}");
        }
        await AssertDeletedAsync(deletionResponse, rows, controlId, controlBefore);
    }

    [Theory]
    [MemberData(nameof(DeletionFirstScenarios))]
    public async Task Delete_HoldsExclusiveAccess_LaterOperationGets401AndRecreatesNothing(string scenarioName)
    {
        // Arrange
        var scenario = _scenarios[scenarioName];
        var barrier = new QueryBarrier(DeletionHoldsExclusive);
        await using var otherHost = CreateHost();
        await using var deletionHost = CreateHost(barrier);
        var (userId, notebook, rows) = await SeedAccountAsync();
        var (controlId, _, _) = await SeedAccountAsync();
        var controlBefore = await DeletionTestSupport.SnapshotAccountAsync(db, controlId);
        using var otherClient = otherHost.ClientFor(userId);
        using var deletionClient = deletionHost.ClientFor(userId);

        // Act: the operation arrives while the deletion is mid-transaction.
        var deletion = DeletionTestSupport.DeleteAsync(deletionClient);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var other = scenario.Send(otherClient, notebook);
        await db.WaitForLockWaitersAsync(userId, 1);
        barrier.Release();
        var deletionResponse = await deletion;
        var otherResponse = await other;

        // Assert: the waiting operation re-checked after the commit and found no account.
        Assert.Equal(HttpStatusCode.Unauthorized, otherResponse.StatusCode);
        await AssertDeletedAsync(deletionResponse, rows, controlId, controlBefore);
    }

    [Fact]
    public async Task Delete_PasswordChangeHoldsExclusiveAccess_DeletionWithOldTokenGets401()
    {
        // Arrange: the password change pauses at its UPDATE, holding exclusive access.
        var barrier = new QueryBarrier("UPDATE users");
        await using var changeHost = CreateHost(barrier);
        await using var deletionHost = CreateHost();
        var (userId, _, _) = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var changeClient = changeHost.ClientFor(userId);
        using var deletionClient = deletionHost.ClientFor(userId);

        // Act
        var change = changeClient.PostAsJsonAsync("/auth/change-password",
            new { currentPassword = TwoHostGymNotebookFixture.Password, newPassword = "a-new-password-1234" });
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var deletion = DeletionTestSupport.DeleteAsync(deletionClient);
        await db.WaitForLockWaitersAsync(userId, 1);
        barrier.Release();
        var changeResponse = await change;
        var deletionResponse = await deletion;

        // Assert: the change revoked the token the deletion carried, so nothing is deleted;
        // only the password hash and token version moved.
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deletionResponse.StatusCode);
        var after = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Skip(1), after.Skip(1));
    }

    [Fact]
    public async Task Delete_HoldsExclusiveAccess_WaitingPasswordChangeGets401()
    {
        // Arrange
        var barrier = new QueryBarrier(DeletionHoldsExclusive);
        await using var changeHost = CreateHost();
        await using var deletionHost = CreateHost(barrier);
        var (userId, _, rows) = await SeedAccountAsync();
        var (controlId, _, _) = await SeedAccountAsync();
        var controlBefore = await DeletionTestSupport.SnapshotAccountAsync(db, controlId);
        using var changeClient = changeHost.ClientFor(userId);
        using var deletionClient = deletionHost.ClientFor(userId);

        // Act: the change verified the password against the still-existing row, then waits.
        var deletion = DeletionTestSupport.DeleteAsync(deletionClient);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var change = changeClient.PostAsJsonAsync("/auth/change-password",
            new { currentPassword = TwoHostGymNotebookFixture.Password, newPassword = "a-new-password-1234" });
        await db.WaitForLockWaitersAsync(userId, 1);
        barrier.Release();
        var deletionResponse = await deletion;
        var changeResponse = await change;

        // Assert: no new token for an account that no longer exists.
        Assert.Equal(HttpStatusCode.Unauthorized, changeResponse.StatusCode);
        await AssertDeletedAsync(deletionResponse, rows, controlId, controlBefore);
    }

    [Fact]
    public async Task Delete_ExportStreaming_DeletionDoesNotWaitAndStreamIsCut()
    {
        // Arrange: the export paused between table queries on real Kestrel, two rows per
        // chunk, as in ExportCoordinationTests — but against the real deletion endpoint.
        var barrier = new QueryBarrier("FROM set_entries AS");
        await using var exportHost = db.CreateHost(
            ExportTestSupport.FlagOn(("Lifecycle:ExportBatchSize", "2")),
            kestrel: o => o.Listen(IPAddress.Loopback, 0),
            services: s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(barrier)));
        await using var deletionHost = CreateHost();
        var (userId, _, rows) = await SeedAccountAsync();
        using var exportClient = exportHost.ClientFor(userId);
        using var deletionClient = deletionHost.ClientFor(userId);

        // Act
        using var export = await ExportTestSupport.ExportAsync(exportClient, completion: HttpCompletionOption.ResponseHeadersRead);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var deletionResponse = await DeletionTestSupport.DeleteAsync(deletionClient).WaitAsync(TimeSpan.FromSeconds(10));
        barrier.Release();
        var (body, error) = await ReadToEndAsync(export);

        // Assert: the deletion went straight through (the export holds no account lock
        // while streaming), and no set — the chunk after it — reached the client.
        Assert.Equal(HttpStatusCode.OK, deletionResponse.StatusCode);
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, rows));
        Assert.NotNull(error);
        Assert.DoesNotContain("\"sets\":[{", body);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(body));
    }

    [Fact]
    public async Task Delete_SlowClientHoldsSharedAccess_DeletionProceedsAfterWriteTimeout()
    {
        // Arrange: a read of a several-MB workout whose client stops reading, on real
        // Kestrel with its own stalled-response detection off, so only the filter's 1 s
        // write timeout can end it (spike A3).
        await using var readHost = db.CreateHost(
            ExportTestSupport.FlagOn(("Lifecycle:WriteTimeoutMs", "1000")),
            kestrel: o =>
            {
                o.Listen(IPAddress.Loopback, 0);
                o.Limits.MinResponseDataRate = null;
            });
        await using var deletionHost = CreateHost();
        var userId = await db.SeedUserAsync();
        var workoutId = await db.SeedHugeWorkoutAsync(userId, blocks: 40, setsPerBlock: 2500);
        var rows = await DeletionTestSupport.RowIdsAsync(db, userId);
        using var readClient = readHost.ClientFor(userId);
        using var deletionClient = deletionHost.ClientFor(userId);

        using var read = await readClient.GetAsync($"/workouts/{workoutId}", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await read.Content.ReadAsStreamAsync();
        await stream.ReadAtLeastAsync(new byte[64 * 1024], 64 * 1024);
        var heldShared = await db.CountLifecycleLocksAsync(userId, granted: true);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var deletionResponse = await DeletionTestSupport.DeleteAsync(deletionClient);
        stopwatch.Stop();

        // Assert: the deletion waited for the stalled read, but only about as long as the
        // write timeout — well inside its own 15 s exclusive wait.
        Assert.Equal(1, heldShared);
        Assert.Equal(HttpStatusCode.OK, deletionResponse.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"deletion took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, rows));
    }

    [Fact]
    public async Task Delete_ExclusiveWaitExceedsTimeout_Returns503AndChangesNothing()
    {
        // Arrange: a 500 ms exclusive wait (the write timeout must stay below it), and a
        // connection that holds shared access for the whole test.
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(
            ExportTestSupport.FlagOn(("Lifecycle:ExclusiveLockTimeoutMs", "500"), ("Lifecycle:WriteTimeoutMs", "200")),
            services: s => s.AddSingleton<ILoggerProvider>(logs));
        var (userId, _, _) = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(@ns, @id)", connection, transaction))
        {
            command.Parameters.AddWithValue("ns", AccountLifecycle.LockNamespace);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        var stopwatch = Stopwatch.StartNew();
        var response = await DeletionTestSupport.DeleteAsync(client);
        stopwatch.Stop();

        // Assert: safe to retry, nothing deleted, and no intent line — the deletion never
        // started, so the restore procedure has nothing to reconcile.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(450), $"gave up after {stopwatch.ElapsedMilliseconds} ms, before the lock wait");
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
        Assert.Empty(logs.DeletionEvents());
    }

    [Fact]
    public async Task Delete_UsernameRegisteredAgain_IsNewAccountThatInheritsNothing()
    {
        // Arrange: A deleted.
        await using var host = CreateHost();
        var (userId, _, _) = await SeedAccountAsync();
        User deletedAccount;
        await using (var context = db.NewContext())
        {
            deletedAccount = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        }
        using var oldSession = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await DeletionTestSupport.DeleteAsync(oldSession)).StatusCode);

        // Act: someone registers the freed username.
        using var anonymous = host.CreateClient();
        var registered = await anonymous.PostAsJsonAsync("/auth/register",
            new { username = deletedAccount.Username, password = "a-new-password-1234", inviteCode = (string?)null });

        // Assert: a different account in every identity that matters, with an empty
        // notebook, and the old session doesn't reach it.
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        await using var check = db.NewContext();
        var newAccount = await check.Users.AsNoTracking().SingleAsync(u => u.Username == deletedAccount.Username);
        Assert.NotEqual(deletedAccount.Id, newAccount.Id);
        Assert.NotEqual(deletedAccount.PrivacyAccountId, newAccount.PrivacyAccountId);
        Assert.False(await check.Exercises.AnyAsync(e => e.UserId == newAccount.Id));
        Assert.False(await check.Workouts.AnyAsync(w => w.UserId == newAccount.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/auth/me")).StatusCode);
    }

    private LifecycleTestHost CreateHost(QueryBarrier? barrier = null) =>
        db.CreateHost(
            ExportTestSupport.FlagOn(),
            services: barrier is null ? null : s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(barrier)));

    // Two workouts × two blocks × two sets, over two exercises, and the ids the scenarios
    // address.
    private async Task<(int UserId, SeededNotebook Notebook, AccountRowIds Rows)> SeedAccountAsync()
    {
        var userId = await db.SeedUserAsync();
        var workoutIds = await db.SeedNotebookAsync(userId, workouts: 2, blocksPerWorkout: 2, setsPerBlock: 2);
        await using var context = db.NewContext();
        var exerciseIds = await context.Exercises.Where(e => e.UserId == userId).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
        var setId = await context.SetEntries
            .Where(s => context.WorkoutExercises.Any(we => we.Id == s.WorkoutExerciseId && we.WorkoutId == workoutIds[0]))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .FirstAsync();
        return (userId, new SeededNotebook(workoutIds[0], setId, exerciseIds[0], exerciseIds[1]), await DeletionTestSupport.RowIdsAsync(db, userId));
    }

    private async Task AssertDeletedAsync(HttpResponseMessage deletionResponse, AccountRowIds rows, int controlId, List<string> controlBefore)
    {
        Assert.Equal(HttpStatusCode.OK, deletionResponse.StatusCode);
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, rows));
        Assert.Equal(controlBefore, await DeletionTestSupport.SnapshotAccountAsync(db, controlId));
    }

    private static async Task<HttpResponseMessage> AcknowledgeAsync(HttpClient client, SeededNotebook _)
    {
        var notice = await client.GetFromJsonAsync<PrivacyNoticeResponse>("/privacy/notice");
        return await client.PutAsJsonAsync("/account/privacy/acknowledgement", new { noticeVersion = notice!.Version });
    }

    // Reads the body until it ends or the connection breaks.
    private static async Task<(string Body, Exception? Error)> ReadToEndAsync(HttpResponseMessage response)
    {
        var received = new MemoryStream();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(received);
            return (Encoding.UTF8.GetString(received.ToArray()), null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return (Encoding.UTF8.GetString(received.ToArray()), ex);
        }
    }
}
