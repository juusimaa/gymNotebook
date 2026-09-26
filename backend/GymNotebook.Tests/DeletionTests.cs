using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GymNotebook.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymNotebook.Tests;

// specs/001 user story 4 (tasks.md T056): what a deletion request does to the database and
// to the account's sessions, and what it answers. Each test builds its own host, so each
// starts with fresh in-process rate-limit counters.
[Collection("Lifecycle")]
public class DeletionTests(TwoHostGymNotebookFixture db)
{
    [Fact]
    public async Task Delete_MissingConfirmation_Returns400AndChangesNothing()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);

        // Act: the password alone, no confirmDeletion at all.
        var response = await client.PostAsJsonAsync("/account/delete", new { currentPassword = TwoHostGymNotebookFixture.Password });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
    }

    [Fact]
    public async Task Delete_FalseConfirmation_Returns400AndChangesNothing()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client, confirm: false);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
    }

    [Fact]
    public async Task Delete_WrongPassword_Returns400PasswordVerificationFailedAndChangesNothing()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);

        // Act: the right password with a trailing space is a wrong password — never trimmed.
        var wrong = await DeletionTestSupport.DeleteAsync(client, "not-the-password");
        var padded = await DeletionTestSupport.DeleteAsync(client, $"{TwoHostGymNotebookFixture.Password} ");

        // Assert: and the session still works, unlike after a real deletion.
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("password_verification_failed", (await wrong.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(wrong.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.BadRequest, padded.StatusCode);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Delete_ClientCancelsWhileWaitingForAccess_ChangesNothing()
    {
        // Arrange: a stand-in holds exclusive access, so the deletion queues behind it.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);
        var holder = await db.HoldExclusiveAsync(userId);

        // Act: the user gives up while the request is still waiting, then the holder ends.
        using var cancel = new CancellationTokenSource();
        var pending = DeletionTestSupport.DeleteAsync(client, cancellationToken: cancel.Token);
        await db.WaitForLockWaitersAsync(userId, 1);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await TwoHostGymNotebookFixture.WaitForAsync(
            async () => await db.CountLifecycleLocksAsync(userId, granted: false) == 0,
            TimeSpan.FromSeconds(10), "the cancelled deletion to stop waiting");
        await holder.DisposeAsync();

        // Assert: nothing was deleted, and nothing is still queued to delete it.
        await TwoHostGymNotebookFixture.WaitForAsync(
            async () => await db.CountLifecycleLocksAsync(userId, granted: true) == 0,
            TimeSpan.FromSeconds(10), "every lifecycle lock to be released");
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
    }

    [Fact]
    public async Task Delete_Success_RemovesAllAccountRowsAndLeavesOtherAccountUnchanged()
    {
        // Arrange: A with a notebook and a notice acknowledgement; B, the control account.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        var otherId = await SeedAccountAsync();
        var aRows = await DeletionTestSupport.RowIdsAsync(db, userId);
        var otherBefore = await DeletionTestSupport.SnapshotAccountAsync(db, otherId);
        using var client = host.ClientFor(userId);

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client);

        // Assert: every row A had is gone — including blocks and sets, found by their ids
        // since they carry no user id — and B is value-for-value what it was.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(aRows.SetIds);
        Assert.Equal(0, await DeletionTestSupport.CountRemainingAsync(db, aRows));
        Assert.Equal(otherBefore, await DeletionTestSupport.SnapshotAccountAsync(db, otherId));
    }

    [Fact]
    public async Task Delete_Success_EveryOldTokenGets401OnEveryProtectedRoute()
    {
        // Arrange: two sessions of A, both valid before the deletion.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        using var deleting = host.ClientFor(userId);
        using var otherSession = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await otherSession.GetAsync("/auth/me")).StatusCode);
        var routes = DeletionTestSupport.ProtectedRoutes(host);

        // Act
        var deleted = await DeletionTestSupport.DeleteAsync(deleting);
        var statuses = new List<(string Route, HttpStatusCode Status)>();
        foreach (var client in new[] { deleting, otherSession })
        {
            foreach (var (method, path) in routes)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method != "GET")
                {
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                }
                statuses.Add(($"{method} {path}", (await client.SendAsync(request)).StatusCode));
            }
        }

        // Assert: not vacuous — the notebook and account routes are all there — and
        // every one of them refuses both old tokens.
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Contains(routes, r => r.Path.StartsWith("/workouts", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Path == "/account/delete");
        Assert.All(statuses, s => Assert.True(s.Status == HttpStatusCode.Unauthorized, $"{s.Route} answered {s.Status}"));
    }

    [Fact]
    public async Task Delete_LostResponseRetried_Returns401()
    {
        // Arrange: the first request deleted the account, but its response never arrived.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedAccountAsync();
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await DeletionTestSupport.DeleteAsync(client)).StatusCode);

        // Act
        var retry = await DeletionTestSupport.DeleteAsync(client);

        // Assert: a bare 401 — nothing the client could mistake for a second success.
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.Equal(0, retry.Content.Headers.ContentLength ?? 0);
    }

    [Fact]
    public async Task Delete_Success_ResponseBodyMatchesContract()
    {
        // Arrange
        var boundary = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(),
            services: s => s.AddSingleton<TimeProvider>(new FixedTimeProvider(boundary)));
        var userId = await SeedAccountAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client);

        // Assert: exactly the contract's fields — in particular no token and nothing
        // about the account (contracts/api.md → Deletion response).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            ["backupsExpireBy", "deletionEvidenceExpiresBy", "logRetentionNotice", "retentionBoundaryAt", "status"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("deleted", root.GetProperty("status").GetString());
        Assert.Equal(boundary, root.GetProperty("retentionBoundaryAt").GetDateTimeOffset());
        Assert.Equal(AccountDeletion.LogRetentionNotice, root.GetProperty("logRetentionNotice").GetString());
    }

    // SC-006: the deadlines stay within 30 calendar days (backups) and 31 days (deletion
    // evidence) of the boundary, including across month ends, a leap day and a year end,
    // where month-based arithmetic would drift (1 July + 1 month is 31 days later). Each
    // row gives the latest instant each deadline may be, worked out by hand on a calendar.
    [Theory]
    [InlineData("2026-01-31T12:00:00Z", "2026-03-02T12:00:00Z", "2026-03-03T12:00:00Z")]
    [InlineData("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", "2026-08-01T00:00:00Z")]
    [InlineData("2028-02-29T23:59:59Z", "2028-03-30T23:59:59Z", "2028-03-31T23:59:59Z")]
    [InlineData("2026-12-31T23:30:00Z", "2027-01-30T23:30:00Z", "2027-01-31T23:30:00Z")]
    public async Task Delete_ControlledClock_DeadlinesWithinRetentionLimits(string boundaryText, string latestBackups, string latestEvidence)
    {
        // Arrange
        var boundary = DateTimeOffset.Parse(boundaryText, System.Globalization.CultureInfo.InvariantCulture);
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(),
            services: s => s.AddSingleton<TimeProvider>(new FixedTimeProvider(boundary)));
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var body = await (await DeletionTestSupport.DeleteAsync(client)).Content.ReadFromJsonAsync<DeletionResponse>();

        // Assert
        Assert.NotNull(body);
        Assert.Equal(boundary, body.RetentionBoundaryAt);
        Assert.InRange(body.BackupsExpireBy, boundary, DateTimeOffset.Parse(latestBackups, System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(body.DeletionEvidenceExpiresBy, body.BackupsExpireBy, DateTimeOffset.Parse(latestEvidence, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Delete_FlagOff_Returns404()
    {
        // Arrange: the fixture's default configuration, with the feature switched off.
        await using var host = db.CreateHost();
        var userId = await SeedAccountAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);

        // Act
        var response = await DeletionTestSupport.DeleteAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
    }

    // An account with something in every table deletion must clear, and an acknowledgement.
    private async Task<int> SeedAccountAsync()
    {
        var userId = await db.SeedUserAsync();
        await db.SeedNotebookAsync(userId, workouts: 2, blocksPerWorkout: 2, setsPerBlock: 2);
        await db.ExecuteAsync(
            "UPDATE users SET acknowledged_privacy_notice_version = 'test-notice', privacy_notice_acknowledged_at = now() WHERE id = @id",
            userId);
        return userId;
    }
}

// A clock stopped at one instant, for deadline arithmetic that must not depend on when the
// test runs. The built-in TimeProvider is the seam; no testing package needed.
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// Every row id an account has, per table: blocks and sets carry no user id, so after a
// deletion they can only be looked for by the ids they had.
internal sealed record AccountRowIds(int UserId, int[] ExerciseIds, int[] WorkoutIds, int[] BlockIds, int[] SetIds);

internal static class DeletionTestSupport
{
    public static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, string password = TwoHostGymNotebookFixture.Password, bool confirm = true,
        CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/account/delete", new { currentPassword = password, confirmDeletion = confirm }, cancellationToken);

    // Every column of every row the account owns, as JSON text in a fixed order: two
    // snapshots are equal exactly when nothing about the account changed.
    public static async Task<List<string>> SnapshotAccountAsync(TwoHostGymNotebookFixture db, int userId)
    {
        const string sql =
            """
            SELECT row_to_json(u)::text FROM users u WHERE u.id = @id
            UNION ALL SELECT * FROM (SELECT row_to_json(e)::text FROM exercises e WHERE e.user_id = @id ORDER BY e.id) x
            UNION ALL SELECT * FROM (SELECT row_to_json(w)::text FROM workouts w WHERE w.user_id = @id ORDER BY w.id) x
            UNION ALL SELECT * FROM (SELECT row_to_json(we)::text FROM workout_exercises we JOIN workouts w ON w.id = we.workout_id WHERE w.user_id = @id ORDER BY we.id) x
            UNION ALL SELECT * FROM (SELECT row_to_json(s)::text FROM set_entries s JOIN workout_exercises we ON we.id = s.workout_exercise_id JOIN workouts w ON w.id = we.workout_id WHERE w.user_id = @id ORDER BY s.id) x
            """;
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    public static async Task<AccountRowIds> RowIdsAsync(TwoHostGymNotebookFixture db, int userId)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        async Task<int[]> IdsAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", userId);
            return (int[])(await command.ExecuteScalarAsync())!;
        }
        return new AccountRowIds(
            userId,
            await IdsAsync("SELECT coalesce(array_agg(id), '{}') FROM exercises WHERE user_id = @id"),
            await IdsAsync("SELECT coalesce(array_agg(id), '{}') FROM workouts WHERE user_id = @id"),
            await IdsAsync("SELECT coalesce(array_agg(we.id), '{}') FROM workout_exercises we JOIN workouts w ON w.id = we.workout_id WHERE w.user_id = @id"),
            await IdsAsync("SELECT coalesce(array_agg(s.id), '{}') FROM set_entries s JOIN workout_exercises we ON we.id = s.workout_exercise_id JOIN workouts w ON w.id = we.workout_id WHERE w.user_id = @id"));
    }

    // Rows still present from `ids`, in any table, plus anything now owned by the user id
    // (a write that slipped in after the snapshot of ids). Zero means the account is gone.
    public static async Task<int> CountRemainingAsync(TwoHostGymNotebookFixture db, AccountRowIds ids)
    {
        const string sql =
            """
            SELECT (SELECT count(*) FROM users WHERE id = @user)
                 + (SELECT count(*) FROM exercises WHERE id = ANY(@exercises) OR user_id = @user)
                 + (SELECT count(*) FROM workouts WHERE id = ANY(@workouts) OR user_id = @user)
                 + (SELECT count(*) FROM workout_exercises WHERE id = ANY(@blocks) OR workout_id = ANY(@workouts))
                 + (SELECT count(*) FROM set_entries WHERE id = ANY(@sets) OR workout_exercise_id = ANY(@blocks))
            """;
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user", ids.UserId);
        command.Parameters.AddWithValue("exercises", ids.ExerciseIds);
        command.Parameters.AddWithValue("workouts", ids.WorkoutIds);
        command.Parameters.AddWithValue("blocks", ids.BlockIds);
        command.Parameters.AddWithValue("sets", ids.SetIds);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // Every authorized route the host maps, with route parameters filled in (the token is
    // what's under test, so any id will do), as (method, path).
    public static List<(string Method, string Path)> ProtectedRoutes(LifecycleTestHost host) =>
        host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (method, "/" + string.Join('/', e.RoutePattern.PathSegments.Select(segment =>
                    segment.IsSimple && segment.Parts[0] is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart literal ? literal.Content : "1")))))
            .Distinct()
            .ToList();
}

// Collects every log entry the host writes, with its structured state, so tests can check
// both which deletion log lines were written and that nothing personal went into any line.
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToList();

    // The deletion's evidence lines, in order: entries of AccountDeletion's category that
    // carry an Event property.
    public List<Entry> DeletionEvents() =>
        Entries.Where(e => e.Category == typeof(AccountDeletion).FullName && e.State.Any(p => p.Key == "Event")).ToList();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), properties, exception));
        }
    }
}
