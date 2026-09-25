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
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(), kestrel: o => o.Listen(IPAddress.Loopback, 0));
        var userId = await db.SeedUserAsync();
        await db.SeedReferenceNotebookAsync(userId);
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
