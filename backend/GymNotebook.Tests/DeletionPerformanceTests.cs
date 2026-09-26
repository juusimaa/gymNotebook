using System.Data.Common;
using System.Diagnostics;
using System.Net;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GymNotebook.Tests;

// specs/001 user story 4 (tasks.md T059): the SC-005 reference notebook — 1,000 workouts ×
// 10 blocks × 10 sets = 100,000 sets — deleted within 60 s. A local regression check only:
// the SC-005 evidence comes from the deployed run (analysis U1), where the database is a
// network round trip away.
//
// Two durations are recorded separately (quickstart §3.7), so contention and database
// speed can be told apart: the wait (request start → the first DELETE, which covers the
// password check and the exclusive lock wait) and the deletion itself (first DELETE →
// commit). A test-only interceptor timestamps both; the API has no timing hooks.
[Collection("Lifecycle")]
public class DeletionPerformanceTests(TwoHostGymNotebookFixture db, ITestOutputHelper output)
{
    [Fact]
    public async Task Delete_ReferenceNotebook_CompletesWithin60Seconds()
    {
        // Arrange
        var probe = new DeletionTimingProbe();
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(),
            services: s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(probe)));
        var userId = await db.SeedUserAsync();
        await db.SeedReferenceNotebookAsync(userId);
        var rows = await DeletionTestSupport.RowIdsAsync(db, userId);
        using var client = host.ClientFor(userId);

        // Act
        probe.Start();
        var stopwatch = Stopwatch.StartNew();
        var response = await DeletionTestSupport.DeleteAsync(client);
        stopwatch.Stop();

        // Assert
        var wait = probe.FirstDeleteAt!.Value;
        var deletion = probe.CommittedAt!.Value - wait;
        output.WriteLine($"total {stopwatch.Elapsed.TotalSeconds:F2} s; wait (password check + lock) {wait.TotalSeconds:F2} s; " +
                         $"delete + commit {deletion.TotalSeconds:F2} s; {rows.SetIds.Length} sets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100_000, rows.SetIds.Length);
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, rows));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
    }

    // Time since Start() (called just before the request) at which the deletion's first
    // DELETE started, and at which its transaction committed.
    private sealed class DeletionTimingProbe : DbCommandInterceptor, IDbTransactionInterceptor
    {
        private readonly Stopwatch _clock = new();

        public void Start() => _clock.Restart();

        public TimeSpan? FirstDeleteAt { get; private set; }

        public TimeSpan? CommittedAt { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FirstDeleteAt is null && command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal))
            {
                FirstDeleteAt = _clock.Elapsed;
            }
            return ValueTask.FromResult(result);
        }

        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            CommittedAt ??= _clock.Elapsed;
            return Task.CompletedTask;
        }
    }
}
