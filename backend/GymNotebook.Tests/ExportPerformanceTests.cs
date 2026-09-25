using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace GymNotebook.Tests;

// specs/001 user story 3 (tasks.md T047): the SC-003 reference notebook — exactly 1,000
// workouts × 10 blocks × 10 sets = 100,000 sets — exported whole within 60 s. A local
// regression check only: the SC-003 evidence comes from the deployed run (analysis U1),
// where the database is a network round trip away. Duration, payload size and memory are
// approximations from the test process (it hosts the app and the client), and
// are written to the test output so a slowdown is visible before it fails the bound.
[Collection("Lifecycle")]
public class ExportPerformanceTests(TwoHostGymNotebookFixture db, ITestOutputHelper output)
{
    [Fact]
    public async Task Export_ReferenceNotebook_CompletesWithin60Seconds()
    {
        // Arrange: seeded in SQL, so seeding takes seconds rather than minutes.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(), kestrel: o => o.Listen(IPAddress.Loopback, 0));
        var userId = await db.SeedUserAsync();
        await db.ExecuteAsync(
            "INSERT INTO exercises (user_id, name, normalized_name, is_bodyweight) SELECT @id, 'Exercise ' || n, 'exercise ' || n, false FROM generate_series(0, 9) AS n",
            userId);
        await db.ExecuteAsync(
            "INSERT INTO workouts (user_id, date, started_at, ended_at) SELECT @id, DATE '2020-01-01' + n, TIMESTAMPTZ '2020-01-01 08:00Z' + n * INTERVAL '1 day', TIMESTAMPTZ '2020-01-01 09:00Z' + n * INTERVAL '1 day' FROM generate_series(0, 999) AS n",
            userId);
        await db.ExecuteAsync(
            """
            INSERT INTO workout_exercises (workout_id, exercise_id, position)
            SELECT w.id, e.id, row_number() OVER (PARTITION BY w.id ORDER BY e.id) - 1
            FROM workouts w CROSS JOIN exercises e
            WHERE w.user_id = @id AND e.user_id = @id
            """, userId);
        await db.ExecuteAsync(
            """
            INSERT INTO set_entries (workout_exercise_id, set_number, weight, reps, is_warmup)
            SELECT we.id, n, 102.50, 5, false
            FROM workout_exercises we JOIN workouts w ON w.id = we.workout_id CROSS JOIN generate_series(1, 10) AS n
            WHERE w.user_id = @id
            """, userId);
        using var client = host.ClientFor(userId);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        // Act
        var stopwatch = Stopwatch.StartNew();
        using var response = await ExportTestSupport.ExportAsync(client, completion: HttpCompletionOption.ResponseHeadersRead);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        stopwatch.Stop();

        // Assert: complete, not silently truncated, and in time.
        output.WriteLine($"duration {stopwatch.Elapsed.TotalSeconds:F1} s; payload {bytes.Length / 1024.0 / 1024.0:F1} MiB; " +
                         $"allocated during export {(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / 1024.0 / 1024.0:F0} MiB (test process, client included); " +
                         $"working set after {Environment.WorkingSet / 1024.0 / 1024.0:F0} MiB");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(1000, root.GetProperty("workouts").GetArrayLength());
        Assert.Equal(10_000, root.GetProperty("workoutExercises").GetArrayLength());
        Assert.Equal(100_000, root.GetProperty("sets").GetArrayLength());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
    }
}
