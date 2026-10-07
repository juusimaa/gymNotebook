using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymNotebook.Tests;

// specs/001 user story 4 (tasks.md T057): the deletion log lines, which are the restore
// procedure's fallback evidence (research R6, data-model.md → Deletion log lines). Each
// outcome must leave exactly its lines — intent then committed, intent then rolled_back,
// or intent alone when the outcome is unknown — and no line may carry anything but the
// event, the PrivacyAccountId and the boundary.
//
// The host's log level is Warning by default (LifecycleTestHost), so these tests also
// prove appsettings.json keeps the deletion's category at Information: without that pin
// the evidence would silently disappear under a stricter default.
[Collection("Lifecycle")]
public class DeletionLogTests(TwoHostGymNotebookFixture db)
{
    private static readonly string[] _allowedProperties = ["Event", "PrivacyAccountId", "DeletionBoundaryAt", "{OriginalFormat}"];

    [Fact]
    public async Task Delete_Success_LogsIntentThenCommittedWithOnlyAccountIdAndBoundary()
    {
        // Arrange
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(), services: s => s.AddSingleton<ILoggerProvider>(logs));
        var (userId, email, privacyAccountId) = await SeedAsync();
        using var client = host.ClientFor(userId);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client);
        var body = await response.Content.ReadFromJsonAsync<DeletionResponse>();

        // Assert: the two lines, in order, both naming this account and the boundary the
        // response reports.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = logs.DeletionEvents();
        Assert.Equal(["deletion.intent", "deletion.committed"], events.Select(EventName));
        Assert.All(events, e =>
        {
            Assert.Equal(privacyAccountId, Property(e, "PrivacyAccountId"));
            Assert.Equal(body!.RetentionBoundaryAt, Property(e, "DeletionBoundaryAt"));
            Assert.Equal(LogLevel.Information, e.Level);
        });
        AssertNothingPersonal(logs, events, userId, email, token);
    }

    [Fact]
    public async Task Delete_FailureBeforeCommit_LogsRolledBackAndKeepsNotebook()
    {
        // Arrange: the delete of the User row — the last statement before COMMIT — fails.
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(), services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(new FailingCommand("DELETE FROM users")));
        });
        var (userId, email, _) = await SeedAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client);

        // Assert: a known rollback — safe to retry, the notebook whole, the session intact.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var events = logs.DeletionEvents();
        Assert.Equal(["deletion.intent", "deletion.rolled_back"], events.Select(EventName));
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        AssertNothingPersonal(logs, events, userId, email, token);
    }

    [Fact]
    public async Task Delete_LockTimeoutDuringDelete_Returns503LogsRolledBackAndKeepsNotebook()
    {
        // Arrange: a real transient failure rather than an injected one (T099). Another
        // connection holds the User row, so the deletion gets its exclusive access, deletes
        // the workouts and exercises, then waits on DELETE FROM users until the guard's
        // lock_timeout (500 ms here; the write timeout must stay below it) gives up with
        // 55P03, which EF's execution strategy hands back wrapped.
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(
            ExportTestSupport.FlagOn(("Lifecycle:ExclusiveLockTimeoutMs", "500"), ("Lifecycle:WriteTimeoutMs", "200")),
            services: s => s.AddSingleton<ILoggerProvider>(logs));
        var (userId, email, _) = await SeedAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        HttpResponseMessage response;

        // Act
        await using (await DeletionTestSupport.HoldUserRowLockAsync(db, userId))
        {
            response = await DeletionTestSupport.DeleteAsync(client);
        }

        // Assert: the same known rollback as any other pre-commit failure — safe to retry,
        // intent then rolled_back, the notebook whole — and the failure really was the
        // lock timeout, not some other error that happened to take this path.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var events = logs.DeletionEvents();
        Assert.Equal(["deletion.intent", "deletion.rolled_back"], events.Select(EventName));
        var warning = Assert.Single(logs.Entries, e => e.Category == typeof(AccountDeletion).FullName && e.Level == LogLevel.Warning);
        Assert.Equal("55P03", FindPostgresException(warning.Exception)?.SqlState);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        AssertNothingPersonal(logs, events, userId, email, token);
    }

    [Fact]
    public async Task Delete_CommitOutcomeUnknown_Returns503DeletionOutcomeUnknownWithIntentOnly()
    {
        // Arrange: the COMMIT reaches the database and applies, but the app sees it fail —
        // what a connection dropping during the commit looks like from the app's side.
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(), services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(new FailAfterCommit()));
        });
        var (userId, email, _) = await SeedAsync();
        var rows = await DeletionTestSupport.RowIdsAsync(db, userId);
        using var client = host.ClientFor(userId);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client);

        // Assert: neither success nor rollback is claimed, and only the intent line exists,
        // which is how the restore procedure recognizes an unknown outcome. (The data is in
        // fact gone here; the app just couldn't know.)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("deletion_outcome_unknown", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.False(response.Headers.Contains("Retry-After"));
        var events = logs.DeletionEvents();
        Assert.Equal(["deletion.intent"], events.Select(EventName));
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, rows));
        AssertNothingPersonal(logs, events, userId, email, token);
    }

    [Fact]
    public async Task Delete_WrongPassword_LogsNoDeletionLines()
    {
        // Arrange
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(), services: s => s.AddSingleton<ILoggerProvider>(logs));
        var (userId, _, _) = await SeedAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client, "not-the-password");

        // Assert: an intent line is written only once a deletion is really about to happen.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(logs.DeletionEvents());
    }

    private async Task<(int UserId, string Email, Guid PrivacyAccountId)> SeedAsync()
    {
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, workouts: 2, blocksPerWorkout: 2, setsPerBlock: 2);
        await using var context = db.NewContext();
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (userId, user.Email, user.PrivacyAccountId);
    }

    // The PostgresException anywhere in the chain: EF may have wrapped it.
    private static PostgresException? FindPostgresException(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException postgres)
            {
                return postgres;
            }
        }
        return null;
    }

    private static string? EventName(CapturingLoggerProvider.Entry entry) => Property(entry, "Event") as string;

    private static object? Property(CapturingLoggerProvider.Entry entry, string name) =>
        entry.State.Single(p => p.Key == name).Value;

    // The evidence lines hold only the three allowed fields, and nothing the host logged at
    // all — at any level that got through — mentions the email address or the token, or carries
    // the integer user id as a value.
    private static void AssertNothingPersonal(CapturingLoggerProvider logs, List<CapturingLoggerProvider.Entry> events, int userId, string email, string token)
    {
        Assert.All(events, e => Assert.All(e.State, p => Assert.Contains(p.Key, _allowedProperties)));
        Assert.All(logs.Entries, e =>
        {
            Assert.DoesNotContain(email, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(token, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(e.State, p => p.Value is int value && value == userId);
        });
    }

    // Fails the first EF command whose SQL contains `fragment`, before it runs, the way a
    // lost connection or a database error would.
    private sealed class FailingCommand(string fragment) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains(fragment, StringComparison.Ordinal)
                ? throw new NpgsqlException("Injected failure before commit.")
                : ValueTask.FromResult(result);
    }

    // Throws after the host's first transaction commit has been applied.
    private sealed class FailAfterCommit : DbTransactionInterceptor
    {
        private int _armed = 1;

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _armed, 0) == 1
                ? throw new NpgsqlException("Injected failure after commit.")
                : Task.CompletedTask;
    }
}
