using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using GymNotebook.Api;
using Xunit.Abstractions;

namespace GymNotebook.Tests.SpikeR4;

// SPIKE A1: overhead of the shared guard under load. 20 concurrent clients against one
// host with the guard off and hosts with it on (two-statement and batch variants), each
// on its own connection pool capped at 20. Pass criterion (research R4 → Q4): p95
// increase ≤ 10 ms locally and the pool never exhausted.
//
// Latencies are printed, not asserted — laptop + Docker timing is too noisy for a CI
// assertion. Errors (including Npgsql's pool-exhaustion timeout, which surfaces as a 500)
// are asserted to be zero.
[Collection("SpikeR4")]
public class A1OverheadTests(SpikeR4Database db, ITestOutputHelper output)
{
    private const int Clients = 20;
    private const int RequestsPerClient = 50;
    private const int Rounds = 3;

    private sealed record Sample(string Host, string Operation, double Milliseconds, bool Ok);

    [Fact]
    public async Task Overhead_GuardOffVsOn_20ConcurrentClients_ReportsLatencyAndNoErrors()
    {
        // Arrange
        var hooks = new SpikeHooks();
        string Pool(string name) => $"{db.ConnectionString};Maximum Pool Size={Clients};Timeout=15;Application Name={name}";
        var hosts = new (string Name, SpikeHost Host)[]
        {
            ("off", db.CreateHost(hooks, new Dictionary<string, string?> { ["Spike:R4:Guard"] = "false" }, connectionString: Pool("a1-off"))),
            ("on-two", db.CreateHost(hooks, new Dictionary<string, string?> { ["Spike:R4:Guard"] = "true", ["Spike:R4:Variant"] = "TwoStatements" }, connectionString: Pool("a1-two"))),
            ("on-batch", db.CreateHost(hooks, new Dictionary<string, string?> { ["Spike:R4:Guard"] = "true", ["Spike:R4:Variant"] = "Batch" }, connectionString: Pool("a1-batch"))),
        };
        var userId = await db.SeedUserAsync();
        var workoutId = (await db.SeedNotebookAsync(userId, 30, 3, 4)).First();
        var clients = hosts.ToDictionary(h => h.Name, h => h.Host.ClientFor(userId));

        // Create the "Row" exercise and its block once, so concurrent POSTs never race
        // get-or-create (that race exists without the guard too and isn't what's measured).
        (await clients["off"].PostAsJsonAsync($"/workouts/{workoutId}/sets", _setBody)).EnsureSuccessStatusCode();

        foreach (var (name, _) in hosts)
        {
            await RunAsync(name, "warm-up", clients[name], c => c.GetAsync("/workouts"), perClient: 10, new ConcurrentBag<Sample>());
        }

        // Act: alternate host order per round so drift (caches, table growth) spreads evenly.
        var samples = new ConcurrentBag<Sample>();
        var stopwatch = Stopwatch.StartNew();
        for (var round = 0; round < Rounds; round++)
        {
            var order = round % 2 == 0 ? hosts : hosts.Reverse().ToArray();
            foreach (var (name, _) in order)
            {
                await RunAsync(name, "GET /workouts", clients[name], c => c.GetAsync("/workouts"), RequestsPerClient, samples);
                await RunAsync(name, "POST /sets", clients[name], c => c.PostAsJsonAsync($"/workouts/{workoutId}/sets", _setBody), RequestsPerClient, samples);
            }
        }
        stopwatch.Stop();

        // Assert + report
        output.WriteLine($"{Clients} clients × {RequestsPerClient} requests × {Rounds} rounds per host/operation; pool max {Clients}; total {stopwatch.Elapsed.TotalSeconds:F1} s");
        output.WriteLine($"{"host",-9} {"operation",-14} {"n",6} {"p50",8} {"p95",8} {"p99",8} {"errors",7}");
        foreach (var group in samples.GroupBy(s => (s.Operation, s.Host)).OrderBy(g => g.Key.Operation).ThenBy(g => g.Key.Host))
        {
            var sorted = group.Select(s => s.Milliseconds).Order().ToArray();
            output.WriteLine($"{group.Key.Host,-9} {group.Key.Operation,-14} {sorted.Length,6} {Percentile(sorted, 50),8:F2} {Percentile(sorted, 95),8:F2} {Percentile(sorted, 99),8:F2} {group.Count(s => !s.Ok),7}");
        }
        foreach (var operation in new[] { "GET /workouts", "POST /sets" })
        {
            var baseline = Percentile(samples.Where(s => s.Host == "off" && s.Operation == operation).Select(s => s.Milliseconds).Order().ToArray(), 95);
            foreach (var host in new[] { "on-two", "on-batch" })
            {
                var guarded = Percentile(samples.Where(s => s.Host == host && s.Operation == operation).Select(s => s.Milliseconds).Order().ToArray(), 95);
                output.WriteLine($"p95 increase, {operation}, {host}: {guarded - baseline:+0.00;-0.00} ms");
            }
        }
        Assert.All(samples, s => Assert.True(s.Ok, $"{s.Host} {s.Operation} failed"));

        foreach (var (_, host) in hosts)
        {
            await host.DisposeAsync();
        }
    }

    private static readonly object _setBody = new { exerciseName = "Row", weight = 60m, reps = 10, isWarmup = false };

    private static async Task RunAsync(string host, string operation, HttpClient client, Func<HttpClient, Task<HttpResponseMessage>> send, int perClient, ConcurrentBag<Sample> samples)
    {
        var workers = Enumerable.Range(0, Clients).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < perClient; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                bool ok;
                try
                {
                    using var response = await send(client);
                    await response.Content.ReadAsByteArrayAsync();
                    ok = response.IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    ok = false;
                }
                samples.Add(new Sample(host, operation, stopwatch.Elapsed.TotalMilliseconds, ok));
            }
        }));
        await Task.WhenAll(workers);
    }

    private static double Percentile(double[] sorted, int percentile) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1)];
}
