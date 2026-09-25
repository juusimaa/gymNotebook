using System.Diagnostics;
using System.Net;
using GymNotebook.Api;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Xunit.Abstractions;

namespace GymNotebook.Tests.SpikeR4;

// SPIKE A2 (deletion during a slow export, across hosts) and A3 (a client that stops
// reading). The streaming host runs on real Kestrel over loopback: TestServer's in-memory
// transport has no socket buffers, so it cannot show back-pressure or a blocked flush.
[Collection("SpikeR4")]
public class A2A3StreamingTests(SpikeR4Database db, ITestOutputHelper output)
{
    // Reads the whole body, counting bytes, until EOF or the connection breaks.
    private sealed class BodyReader(HttpResponseMessage response)
    {
        private long _bytes;
        public long Bytes => Interlocked.Read(ref _bytes);
        public string Tail { get; private set; } = "";
        public string? Error { get; private set; }

        public async Task RunAsync(long stopAfterBytes = long.MaxValue, Task? resume = null)
        {
            var buffer = new byte[16 * 1024];
            var tail = new List<byte>();
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    Interlocked.Add(ref _bytes, read);
                    tail.AddRange(buffer.AsSpan(Math.Max(0, read - 2), Math.Min(2, read)).ToArray());
                    if (tail.Count > 2)
                    {
                        tail.RemoveRange(0, tail.Count - 2);
                    }
                    if (Bytes >= stopAfterBytes && resume is not null)
                    {
                        await resume; // the "client stops reading" pause
                        resume = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name;
            }
            Tail = System.Text.Encoding.ASCII.GetString(tail.ToArray());
        }
    }

    private static string AbortDetail(SpikeHooks hooks) =>
        hooks.Events.FirstOrDefault(e => e.Name == "export.aborted").Detail ?? "(none)";

    [Fact]
    public async Task Export_DeletionCommitsBetweenChunks_AbortsAtNextCheckAndLeavesNoOpenTransaction()
    {
        // Arrange: pause the export after chunk 2 has been handed to the transport.
        var hooks = new SpikeHooks();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.On("export.chunkSent", async i =>
        {
            if (i == 2)
            {
                reached.SetResult();
                await release.Task;
            }
        });
        await using var exportHost = db.CreateHost(hooks, kestrel: true);
        await using var deleteHost = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, 3, 2, 2);
        using var exportClient = exportHost.ClientFor(userId);
        using var deleteClient = deleteHost.ClientFor(userId);

        // Act
        var response = await exportClient.GetAsync("/spike/export?chunks=10&chunkBytes=65536", HttpCompletionOption.ResponseHeadersRead);
        var reader = new BodyReader(response);
        var reading = reader.RunAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopwatch = Stopwatch.StartNew();
        var delete = await deleteClient.PostAsync("/spike/account/delete", null);
        stopwatch.Stop();
        await SpikeR4Database.WaitForAsync(() => reader.Bytes >= 3 * 65536, TimeSpan.FromSeconds(10), "chunks 0–2 received");
        var bytesAtCommit = reader.Bytes;
        release.SetResult();
        await reading.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        output.WriteLine($"delete {(int)delete.StatusCode} in {stopwatch.ElapsedMilliseconds} ms; bytes at commit {bytesAtCommit}, final {reader.Bytes}; tail '{reader.Tail}'; client error {reader.Error ?? "none"}; abort: {AbortDetail(hooks)}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(bytesAtCommit, reader.Bytes); // nothing after the deletion committed
        Assert.NotEqual("\"]", reader.Tail);       // never parses as a complete document
        Assert.NotNull(reader.Error);              // the client sees a failed download
        Assert.StartsWith("guard:Revoked before chunk 3", AbortDetail(hooks));
        await SpikeR4Database.WaitForAsync(() => db.CountIdleInTransactionAsync().Result == 0, TimeSpan.FromSeconds(5), "no idle-in-transaction sessions");
    }

    [Fact]
    public async Task Export_DeletionArrivesWhileChunkGuardHeld_DeletionWaitsThenExportAborts()
    {
        // Arrange: pause inside the third guard call (init guard, chunk 0, chunk 1).
        var hooks = new SpikeHooks();
        var calls = 0;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.On("export.guardHeld", async _ =>
        {
            if (Interlocked.Increment(ref calls) == 3)
            {
                holding.SetResult();
                await release.Task;
            }
        });
        await using var exportHost = db.CreateHost(hooks, kestrel: true);
        await using var deleteHost = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        using var exportClient = exportHost.ClientFor(userId);
        using var deleteClient = deleteHost.ClientFor(userId);

        // Act
        var response = await exportClient.GetAsync("/spike/export?chunks=10&chunkBytes=65536", HttpCompletionOption.ResponseHeadersRead);
        var reader = new BodyReader(response);
        var reading = reader.RunAsync();
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var delete = deleteClient.PostAsync("/spike/account/delete", null);
        await db.WaitForLockWaitersAsync(userId, 1, TimeSpan.FromSeconds(10)); // deletion is queued
        release.SetResult();
        var deleteResponse = await delete.WaitAsync(TimeSpan.FromSeconds(20));
        await reading.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the held chunk (1) goes out, the next check (chunk 2) aborts.
        output.WriteLine($"delete {(int)deleteResponse.StatusCode}; bytes {reader.Bytes}; abort: {AbortDetail(hooks)}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.StartsWith("guard:Revoked before chunk 2", AbortDetail(hooks));
        Assert.NotEqual("\"]", reader.Tail);
    }

    [Fact]
    public async Task Export_ClientStopsReading_HoldsNoLockSoDeletionProceeds_ThenWriteTimesOut()
    {
        // Arrange: Kestrel's own stall detection off, so the app's write timeout is what's measured.
        var hooks = new SpikeHooks();
        await using var exportHost = db.CreateHost(hooks, new Dictionary<string, string?> { ["Spike:R4:WriteTimeoutMs"] = "1000" },
            kestrel: true, kestrelOptions: o => { o.Listen(IPAddress.Loopback, 0); o.Limits.MinResponseDataRate = null; });
        await using var deleteHost = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        using var exportClient = exportHost.ClientFor(userId);
        using var deleteClient = deleteHost.ClientFor(userId);
        var never = new TaskCompletionSource();

        // Act: read 256 KB, then stop reading without disconnecting.
        var response = await exportClient.GetAsync("/spike/export?chunks=400&chunkBytes=262144", HttpCompletionOption.ResponseHeadersRead);
        var reader = new BodyReader(response);
        _ = reader.RunAsync(stopAfterBytes: 256 * 1024, resume: never.Task);
        await SpikeR4Database.WaitForAsync(() => reader.Bytes >= 256 * 1024, TimeSpan.FromSeconds(10), "first 256 KB");
        var stalledAt = Stopwatch.StartNew();
        await SpikeR4Database.WaitForAsync(() => hooks.Events.Count(e => e.Name == "export.chunkSent") < 400 &&
            hooks.Events.LastOrDefault(e => e.Name == "export.chunkSent").Detail is { } last && stalledAt.ElapsedMilliseconds > 300,
            TimeSpan.FromSeconds(10), "writer has had time to block");
        var chunksSentBeforeDelete = hooks.Events.Count(e => e.Name == "export.chunkSent");
        var deleteTimer = Stopwatch.StartNew();
        var delete = await deleteClient.PostAsync("/spike/account/delete", null);
        deleteTimer.Stop();
        await SpikeR4Database.WaitForAsync(() => hooks.Events.Any(e => e.Name == "export.aborted"), TimeSpan.FromSeconds(30), "export aborted");
        var abortedAfter = stalledAt.ElapsedMilliseconds;

        // Assert
        output.WriteLine($"client read {reader.Bytes} bytes; server handed over {chunksSentBeforeDelete} × 256 KB before stalling; delete {(int)delete.StatusCode} in {deleteTimer.ElapsedMilliseconds} ms; export aborted ~{abortedAfter} ms after stall: {AbortDetail(hooks)}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.True(deleteTimer.ElapsedMilliseconds < 1000, "deletion must not wait on a stalled export");
        Assert.StartsWith("write-timeout", AbortDetail(hooks));
        never.SetResult();
        response.Dispose();
    }

    [Theory]
    [InlineData(false, 1500, HttpStatusCode.OK)]  // app write timeout only (Kestrel stall detection off), shortened
    [InlineData(true, 10000, HttpStatusCode.OK)]  // Kestrel defaults + Q4's real 10 s write timeout
    // Kestrel's default MinResponseDataRate does not end the stalled write within deletion's
    // 15 s wait: with a write timeout longer than that wait, deletion gets 503. The app's
    // write timeout is the only bound, and Q4's "write < exclusive wait" ordering is load-bearing.
    [InlineData(true, 30000, HttpStatusCode.ServiceUnavailable)]
    public async Task GuardedRead_ClientStopsReading_WriteTimeoutBoundsSharedHold(bool kestrelDefaults, int writeTimeoutMs, HttpStatusCode expectedDelete)
    {
        // Arrange: a workout whose JSON is several MB, far beyond the socket buffers, so the
        // filter's response write really blocks while it still holds shared access.
        var hooks = new SpikeHooks();
        await using var readHost = db.CreateHost(hooks,
            new Dictionary<string, string?> { ["Spike:R4:Guard"] = "true", ["Spike:R4:WriteTimeoutMs"] = writeTimeoutMs.ToString() },
            kestrel: true, kestrelOptions: o =>
            {
                o.Listen(IPAddress.Loopback, 0);
                if (!kestrelDefaults)
                {
                    o.Limits.MinResponseDataRate = null;
                }
            });
        await using var deleteHost = db.CreateHost(hooks);
        var userId = await db.SeedUserAsync();
        var workoutId = await db.SeedHugeWorkoutAsync(userId, blocks: 40, setsPerBlock: 2500);
        using var readClient = readHost.ClientFor(userId);
        using var deleteClient = deleteHost.ClientFor(userId);
        var never = new TaskCompletionSource();

        // Act
        var response = await readClient.GetAsync($"/workouts/{workoutId}", HttpCompletionOption.ResponseHeadersRead);
        var reader = new BodyReader(response);
        _ = reader.RunAsync(stopAfterBytes: 64 * 1024, resume: never.Task);
        await SpikeR4Database.WaitForAsync(() => reader.Bytes >= 64 * 1024, TimeSpan.FromSeconds(10), "first 64 KB");
        var stalled = Stopwatch.StartNew();
        var delete = deleteClient.PostAsync("/spike/account/delete", null);
        await db.WaitForLockWaitersAsync(userId, 1, TimeSpan.FromSeconds(10)); // proves the read still holds shared access
        var deleteResponse = await delete.WaitAsync(TimeSpan.FromSeconds(30));
        var deletedAfter = stalled.ElapsedMilliseconds;

        // Assert
        output.WriteLine($"kestrelDefaults={kestrelDefaults}, writeTimeout={writeTimeoutMs} ms: client read {reader.Bytes} bytes; delete {(int)deleteResponse.StatusCode} {deletedAfter} ms after stall");
        Assert.Equal(expectedDelete, deleteResponse.StatusCode);
        if (expectedDelete == HttpStatusCode.OK)
        {
            Assert.True(deletedAfter < writeTimeoutMs + 3000, "the stalled read must release shared access within about the write timeout");
        }
        never.SetResult();
        response.Dispose();
    }
}
