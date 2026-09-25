using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GymNotebook.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/001 user story 3 (tasks.md T046): the export against everything else happening to
// the account while it streams (research R3/R4). A QueryBarrier pauses the export between
// two of its table queries; the test acts while it's paused, then lets it go on.
//
// The streaming tests run on real Kestrel over loopback, because an abort must reach a
// real client as a broken download, and a batch size of 2 so that a small notebook still
// streams in several chunks, each behind its own delivery guard.
[Collection("Lifecycle")]
public class ExportCoordinationTests(TwoHostGymNotebookFixture db)
{
    private const string BeforeWorkouts = "FROM workouts AS";
    private const string BeforeSets = "FROM set_entries AS";

    [Fact]
    public async Task Export_WorkoutEditedBetweenTableQueries_FileIsOnePreChangeSnapshot()
    {
        // Arrange
        var barrier = new QueryBarrier(BeforeWorkouts);
        await using var host = CreateHost(barrier, kestrel: false);
        var userId = await db.SeedUserAsync();
        var workoutIds = await db.SeedNotebookAsync(userId, 2, 1, 1);
        using var client = host.ClientFor(userId);

        // Act: while the export sits between its exercises and workouts queries, another
        // connection edits a workout, adds a set and adds a workout, and commits.
        var pending = ExportTestSupport.ExportAsync(client);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await db.ExecuteAsync("UPDATE workouts SET title = 'changed during export' WHERE user_id = @id", userId);
        await db.ExecuteAsync(
            "INSERT INTO set_entries (workout_exercise_id, set_number, weight, reps, is_warmup) SELECT we.id, 99, 1, 1, false FROM workout_exercises we JOIN workouts w ON w.id = we.workout_id WHERE w.user_id = @id",
            userId);
        await db.ExecuteAsync("INSERT INTO workouts (user_id, date, started_at) VALUES (@id, DATE '2026-06-01', TIMESTAMPTZ '2026-06-01 08:00Z')", userId);
        barrier.Release();
        var response = await pending;

        // Assert: none of the concurrent changes, and every reference still resolves.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("changed during export", text);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(workoutIds, root.GetProperty("workouts").EnumerateArray().Select(w => w.GetProperty("id").GetInt32()).ToList());
        var sets = root.GetProperty("sets").EnumerateArray().ToList();
        Assert.Equal(2, sets.Count);
        Assert.DoesNotContain(sets, s => s.GetProperty("setNumber").GetInt32() == 99);
        var blockIds = root.GetProperty("workoutExercises").EnumerateArray().Select(b => b.GetProperty("id").GetInt32()).ToHashSet();
        Assert.All(sets, s => Assert.Contains(s.GetProperty("workoutExerciseId").GetInt32(), blockIds));
    }

    [Fact]
    public async Task Export_DeletionCommitsMidStream_AbortsAtNextChunk()
    {
        // Arrange
        var barrier = new QueryBarrier(BeforeSets);
        await using var host = CreateHost(barrier);
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 3, 2, 2);
        using var client = host.ClientFor(userId);

        // Act: earlier chunks are already with the client when the deletion commits.
        using var response = await ExportTestSupport.ExportAsync(client, completion: HttpCompletionOption.ResponseHeadersRead);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var deletion = await db.HoldExclusiveAsync(userId))
        {
            await deletion.DeleteUserAndCommitAsync();
        }
        barrier.Release();
        var (body, error) = await ReadToEndAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertTruncatedBeforeSets(body, error);
        await AssertNothingLeftOpenAsync(userId);
    }

    [Fact]
    public async Task Export_PasswordChangeCommitsMidStream_AbortsAtNextChunk()
    {
        // Arrange
        var barrier = new QueryBarrier(BeforeSets);
        await using var host = CreateHost(barrier);
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 3, 2, 2);
        using var client = host.ClientFor(userId);

        // Act
        using var response = await ExportTestSupport.ExportAsync(client, completion: HttpCompletionOption.ResponseHeadersRead);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var change = await db.HoldExclusiveAsync(userId))
        {
            await change.BumpTokenVersionAndCommitAsync();
        }
        barrier.Release();
        var (body, error) = await ReadToEndAsync(response);

        // Assert
        AssertTruncatedBeforeSets(body, error);
        await AssertNothingLeftOpenAsync(userId);
    }

    [Fact]
    public async Task Export_TokenExpiresMidStream_AbortsAtNextChunk()
    {
        // Arrange: a clock the test moves past the token's 30-minute expiry.
        var barrier = new QueryBarrier(BeforeSets);
        var clock = new OffsetTimeProvider();
        await using var host = CreateHost(barrier, services: s => s.AddSingleton<TimeProvider>(clock));
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 3, 2, 2);
        using var client = host.ClientFor(userId);

        // Act
        using var response = await ExportTestSupport.ExportAsync(client, completion: HttpCompletionOption.ResponseHeadersRead);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Offset = TimeSpan.FromMinutes(GymNotebookFactory.JwtExpiryMinutes + 1);
        barrier.Release();
        var (body, error) = await ReadToEndAsync(response);

        // Assert
        AssertTruncatedBeforeSets(body, error);
        await AssertNothingLeftOpenAsync(userId);
    }

    [Fact]
    public async Task Export_TimeLimitReached_AbortsStreamAndReleasesSnapshot()
    {
        // Arrange: a 1.5 s cap, and a barrier that is never released — only the cap can
        // end the paused query.
        var barrier = new QueryBarrier(BeforeSets);
        await using var host = CreateHost(barrier, extra: ("Lifecycle:ExportMaxDurationMs", "1500"));
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 3, 2, 2);
        using var client = host.ClientFor(userId);

        // Act
        var stopwatch = Stopwatch.StartNew();
        using var response = await ExportTestSupport.ExportAsync(client, completion: HttpCompletionOption.ResponseHeadersRead);
        var (body, error) = await ReadToEndAsync(response);
        stopwatch.Stop();

        // Assert
        AssertTruncatedBeforeSets(body, error);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(10));
        await AssertNothingLeftOpenAsync(userId);
    }

    [Fact]
    public async Task Export_ClientDisconnects_LeavesNoCopyAndRetryGivesCompleteFile()
    {
        // Arrange
        var barrier = new QueryBarrier(BeforeSets);
        await using var host = CreateHost(barrier);
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 3, 2, 2);
        using var client = host.ClientFor(userId);

        // Act: the client goes away mid-download; then it retries with the password.
        var response = await ExportTestSupport.ExportAsync(client, completion: HttpCompletionOption.ResponseHeadersRead);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        response.Dispose();
        barrier.Release();
        await AssertNothingLeftOpenAsync(userId);
        var retry = await ExportTestSupport.ExportAsync(client);

        // Assert: nothing of the first attempt was kept, and the retry is a fresh, whole file.
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var document = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(12, document.RootElement.GetProperty("sets").GetArrayLength());
    }

    private LifecycleTestHost CreateHost(
        QueryBarrier barrier, bool kestrel = true, Action<IServiceCollection>? services = null,
        params (string Key, string? Value)[] extra) =>
        db.CreateHost(
            ExportTestSupport.FlagOn([("Lifecycle:ExportBatchSize", "2"), .. extra]),
            kestrel: kestrel ? o => o.Listen(IPAddress.Loopback, 0) : null,
            services: s =>
            {
                s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(barrier));
                services?.Invoke(s);
            });

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

    // The download failed visibly, is not a parseable document, and no set — the chunk
    // after the change — reached the client.
    private static void AssertTruncatedBeforeSets(string body, Exception? error)
    {
        Assert.NotNull(error);
        // Earlier chunks did arrive; the first set row didn't. (Not "setNumber": the field
        // guide at the top of the file mentions every field name.)
        Assert.Contains("\"exercises\":[{", body);
        Assert.DoesNotContain("\"sets\":[{", body);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(body));
    }

    // The snapshot and every guard ended: no export lock, no transaction left open.
    private async Task AssertNothingLeftOpenAsync(int userId)
    {
        await TwoHostGymNotebookFixture.WaitForAsync(
            async () => await db.CountExportLocksAsync(userId) == 0 && await db.CountIdleInTransactionAsync() == 0,
            TimeSpan.FromSeconds(10), "the export's snapshot and guards to end");
    }
}
