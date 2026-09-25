using System.Buffers;
using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace GymNotebook.Api;

// POST /account/export (specs/001 user story 3): the caller's whole notebook as one JSON
// file, format version 1 in contracts/api.md. Three requirements shape the code:
//
//   1. One consistent snapshot. Every table is read in a single read-only REPEATABLE READ
//      transaction, so an edit made while the download runs can't produce a file that is
//      half before and half after it (research R3).
//   2. Deletion and revocation still win. That snapshot can't hold the account's lifecycle
//      lock — a download can take a minute, and deletion must not wait that long — so
//      instead every chunk is preceded by a short *delivery guard* on its own connection:
//      shared lifecycle access and a fresh token check, released before the bytes are
//      written. A deletion, password change or token expiry therefore stops the stream at
//      the next chunk (research R4).
//   3. Nothing stored. Rows are read in keyset batches and streamed as they are serialized;
//      no file, row or URL for the export exists anywhere once the request ends
//      (data-model.md → Export document).
//
// A cut-short stream must never look like a finished file, so the closing brace goes out
// only with the last chunk, after the last guard, and every failure after the first byte
// aborts the connection rather than ending the response cleanly.
public sealed class NotebookExport(AppDbContext db, LifecycleOptions options, TimeProvider clock, ILogger<NotebookExport> logger)
{
    // Second key namespace for advisory locks, separate from AccountLifecycle.LockNamespace:
    // the one-export-per-account limit (P12). Taken by the snapshot transaction itself, so it
    // holds across app instances for exactly as long as the export runs, and deletion never
    // touches it. Spells "EXPT" in ASCII.
    public const int ExportLockNamespace = 0x45585054;

    public const string FileName = "gym-notebook-export.json";

    // The user-visible text is stored Unicode, written as-is rather than as \u escapes, so
    // the file reads naturally in an editor. The "unsafe" in the name is about embedding
    // JSON in HTML <script> blocks; this file is only ever a download.
    private static readonly JsonWriterOptions _writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<IResult> RunAsync(ExportRequest request, HttpContext http)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            return Results.BadRequest(new ErrorResponse("invalid_request"));
        }

        var (userId, tokenVersion) = AccountLifecycle.ReadClaims(http.User);
        var expiresAt = ReadExpiry(http.User);

        // The 120 s cap covers everything below, including a client that reads slowly.
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cap.CancelAfter(options.ExportMaxDurationMs);
        var ct = cap.Token;

        // BCrypt is deliberately slow, so the password is checked before any lock is taken,
        // as in change-password. A password change in between shows up under the guard below
        // as a token-version mismatch. AsNoTracking: OnTokenValidated's FindAsync already
        // tracks this User, loaded before any lock.
        var hash = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PasswordHash)
            .SingleOrDefaultAsync(ct);
        if (hash is null)
        {
            return Results.Unauthorized();
        }
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, hash))
        {
            return Results.BadRequest(new ErrorResponse("password_verification_failed"));
        }

        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("AppDbContext has no connection string.");

        // The snapshot lives on the request's own AppDbContext connection. Disposal on any
        // early return or exception rolls it back, which also releases the export lock.
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        // The initialization guard: shared access and a fresh check on a *separate* READ
        // COMMITTED connection, held while the snapshot's first statement runs. So the
        // snapshot is known to have been taken while this account existed with this token
        // version, and no deletion can have committed between the check and the snapshot.
        DateTimeOffset snapshotAt;
        await using (var init = await OwnConnectionGuard.OpenAsync(connectionString, userId, tokenVersion, options.SharedLockTimeoutMs, ct))
        {
            if (init.Outcome != GuardOutcome.Ok)
            {
                return AccountLifecycle.ToResult(init.Outcome, http);
            }
            if (clock.GetUtcNow() >= expiresAt)
            {
                return Results.Unauthorized();
            }

            var started = await StartSnapshotAsync(snapshot, userId, ct);
            if (started is null)
            {
                // Another export of this account is running, on this or another instance.
                // Nothing was read and nothing changed.
                return Results.Json(new ErrorResponse("export_in_progress"), statusCode: StatusCodes.Status429TooManyRequests);
            }
            snapshotAt = started.Value;

            // Released before enumeration: the long snapshot must not keep deletion waiting.
            await init.CommitAsync();
        }

        var stream = new ChunkedResponse(http, connectionString, userId, tokenVersion, expiresAt, options, clock);
        try
        {
            await WriteDocumentAsync(stream, userId, snapshotAt, ct);
        }
        catch (ExportAbortedException ex)
        {
            // Nothing sent yet: the caller can still get a normal status instead of a cut
            // connection. A token that failed its check is the usual bare 401.
            if (!http.Response.HasStarted && ex.Outcome is { } outcome)
            {
                return AccountLifecycle.ToResult(outcome, http);
            }
            return Abort(http, ex.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && ct.IsCancellationRequested)
        {
            return Abort(http, http.RequestAborted.IsCancellationRequested ? "client disconnected" : "export time limit reached");
        }

        // Read-only, so committing just ends the snapshot and releases the export lock.
        await snapshot.CommitAsync(CancellationToken.None);
        return Results.Empty;
    }

    // Everything in the document, in contracts/api.md's order. Each ChunkedResponse.SendAsync
    // call is one guarded chunk; the last one carries the closing brace.
    private async Task WriteDocumentAsync(ChunkedResponse stream, int userId, DateTimeOffset snapshotAt, CancellationToken ct)
    {
        var json = stream.Json;
        var batch = options.ExportBatchSize;

        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.PrivacyAccountId, u.Username, u.CreatedAt, u.AcknowledgedPrivacyNoticeVersion, u.PrivacyNoticeAcknowledgedAt })
            .SingleAsync(ct);

        json.WriteStartObject();
        json.WriteNumber("formatVersion", 1);
        WriteInstant(json, "snapshotAt", snapshotAt);
        json.WritePropertyName("fieldGuide");
        JsonSerializer.Serialize(json, ExportFieldGuide.Content);

        json.WriteStartObject("account");
        json.WriteNumber("id", account.Id);
        json.WriteString("privacyAccountId", account.PrivacyAccountId);
        json.WriteString("username", account.Username);
        WriteInstant(json, "createdAt", account.CreatedAt);
        json.WriteEndObject();

        // Keyset batches rather than OFFSET: each batch starts strictly after the last row
        // of the previous one in the documented order, so it's an index range scan however
        // deep into the notebook the export has got.
        var exercises = db.Exercises.AsNoTracking().Where(e => e.UserId == userId);
        await stream.WriteArrayAsync<ExerciseRow>("exercises",
            last => (last is null ? exercises : exercises.Where(e => e.Id > last.Id))
                .OrderBy(e => e.Id)
                .Take(batch)
                .Select(e => new ExerciseRow(e.Id, e.UserId, e.Name, e.IsBodyweight, e.CreatedAt)),
            row =>
            {
                json.WriteNumber("id", row.Id);
                json.WriteNumber("userId", row.UserId);
                json.WriteString("name", row.Name);
                json.WriteBoolean("isBodyweight", row.IsBodyweight);
                WriteInstant(json, "createdAt", row.CreatedAt);
            }, ct);

        var workouts = db.Workouts.AsNoTracking().Where(w => w.UserId == userId);
        await stream.WriteArrayAsync<WorkoutRow>("workouts",
            last => (last is null ? workouts : workouts.Where(w => w.Id > last.Id))
                .OrderBy(w => w.Id)
                .Take(batch)
                .Select(w => new WorkoutRow(w.Id, w.UserId, w.Date, w.StartedAt, w.EndedAt, w.Title, w.Location, w.Notes, w.BodyweightKg, w.CreatedAt)),
            row =>
            {
                json.WriteNumber("id", row.Id);
                json.WriteNumber("userId", row.UserId);
                // The stored local calendar day, never recomputed from startedAt.
                json.WriteString("date", row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                WriteInstant(json, "startedAt", row.StartedAt);
                WriteInstant(json, "endedAt", row.EndedAt);
                WriteText(json, "title", row.Title);
                WriteText(json, "location", row.Location);
                WriteText(json, "notes", row.Notes);
                WriteDecimal(json, "bodyweightKg", row.BodyweightKg);
                WriteInstant(json, "createdAt", row.CreatedAt);
            }, ct);

        // Blocks and sets carry no user id; ownership is through their workout.
        var blocks = db.WorkoutExercises.AsNoTracking()
            .Where(we => db.Workouts.Any(w => w.Id == we.WorkoutId && w.UserId == userId));
        await stream.WriteArrayAsync<BlockRow>("workoutExercises",
            last => (last is null ? blocks : blocks.Where(we =>
                    EF.Functions.GreaterThan(ValueTuple.Create(we.WorkoutId, we.Position, we.Id), ValueTuple.Create(last.WorkoutId, last.Position, last.Id))))
                .OrderBy(we => we.WorkoutId).ThenBy(we => we.Position).ThenBy(we => we.Id)
                .Take(batch)
                .Select(we => new BlockRow(we.Id, we.WorkoutId, we.ExerciseId, we.Position)),
            row =>
            {
                json.WriteNumber("id", row.Id);
                json.WriteNumber("workoutId", row.WorkoutId);
                json.WriteNumber("exerciseId", row.ExerciseId);
                json.WriteNumber("position", row.Position);
            }, ct);

        // Sets are read one page of the account's blocks at a time (in block id order, which
        // is the sets' leading sort key), then keyset-paged within that page. Joining sets
        // to workouts in one query instead leaves the plan to the statistics: with stale
        // ones PostgreSQL drives it from the workouts and probes every block for every
        // batch, which made the reference export take a minute locally. `= ANY(block ids)`
        // on set_entries' (workout_exercise_id, set_number) index is fast whatever they say.
        var blockIds = blocks.Select(we => we.Id);
        json.WriteStartArray("sets");
        var lastBlockId = 0;
        while (true)
        {
            var page = await blockIds.Where(id => id > lastBlockId).OrderBy(id => id).Take(batch).ToListAsync(ct);
            if (page.Count == 0)
            {
                break;
            }

            var pageSets = db.SetEntries.AsNoTracking().Where(s => page.Contains(s.WorkoutExerciseId));
            SetRow? last = null;
            while (true)
            {
                var rows = await (last is null ? pageSets : pageSets.Where(s =>
                        EF.Functions.GreaterThan(ValueTuple.Create(s.WorkoutExerciseId, s.SetNumber, s.Id), ValueTuple.Create(last.WorkoutExerciseId, last.SetNumber, last.Id))))
                    .OrderBy(s => s.WorkoutExerciseId).ThenBy(s => s.SetNumber).ThenBy(s => s.Id)
                    .Take(batch)
                    .Select(s => new SetRow(s.Id, s.WorkoutExerciseId, s.SetNumber, s.Weight, s.Reps, s.IsWarmup))
                    .ToListAsync(ct);
                await stream.WriteRowsAsync(rows, row =>
                {
                    json.WriteNumber("id", row.Id);
                    json.WriteNumber("workoutExerciseId", row.WorkoutExerciseId);
                    json.WriteNumber("setNumber", row.SetNumber);
                    WriteDecimal(json, "weight", row.Weight);
                    json.WriteNumber("reps", row.Reps);
                    json.WriteBoolean("isWarmup", row.IsWarmup);
                }, ct);
                if (rows.Count < batch)
                {
                    break;
                }
                last = rows[^1];
            }
            lastBlockId = page[^1];
        }
        json.WriteEndArray();

        json.WriteStartObject("privacyRecords");
        if (account.AcknowledgedPrivacyNoticeVersion is null || account.PrivacyNoticeAcknowledgedAt is null)
        {
            json.WriteNull("noticeAcknowledgement");
        }
        else
        {
            json.WriteStartObject("noticeAcknowledgement");
            json.WriteString("noticeVersion", account.AcknowledgedPrivacyNoticeVersion);
            WriteInstant(json, "acknowledgedAt", account.PrivacyNoticeAcknowledgedAt.Value);
            json.WriteEndObject();
        }
        // Always null for now: the consent columns arrive with user story 6 (tasks.md T090),
        // and until then no account can hold a consent. T096 fills this in from the row.
        json.WriteNull("optionalDetailsConsent");
        json.WriteEndObject();

        json.WriteEndObject();
        await stream.SendAsync(ct);
    }

    // The snapshot's first statement, so the database takes the REPEATABLE READ snapshot
    // here: it records the database's own clock as snapshotAt and tries the export lock.
    // SET TRANSACTION READ ONLY must come before it, and establishes no snapshot itself.
    // Returns null when another export of this account holds the lock.
    private async Task<DateTimeOffset?> StartSnapshotAsync(IDbContextTransaction snapshot, int userId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var command = new NpgsqlCommand(
            "SELECT statement_timestamp(), pg_try_advisory_xact_lock(@ns, @id)",
            connection, (NpgsqlTransaction)snapshot.GetDbTransaction());
        command.Parameters.AddWithValue("ns", ExportLockNamespace);
        command.Parameters.AddWithValue("id", userId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetBoolean(1) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    // The bearer handler has validated "exp" already, but the export can outlive it, so each
    // chunk checks it again, exactly and with no clock skew allowance.
    private static DateTimeOffset ReadExpiry(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst("exp")?.Value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : throw new InvalidOperationException("Authenticated user has no valid exp claim.");

    private IResult Abort(HttpContext http, string reason)
    {
        // Reason only: never the user, the password or any exported content.
        logger.LogInformation("Notebook export aborted: {Reason}.", reason);
        http.Abort();
        return Results.Empty;
    }

    // UTC instants, as the rest of the API serializes DateTimeOffset.
    private static void WriteInstant(Utf8JsonWriter json, string name, DateTimeOffset value) =>
        json.WriteString(name, value.ToUniversalTime());

    private static void WriteInstant(Utf8JsonWriter json, string name, DateTimeOffset? value)
    {
        if (value is null) json.WriteNull(name);
        else WriteInstant(json, name, value.Value);
    }

    private static void WriteText(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null) json.WriteNull(name);
        else json.WriteString(name, value);
    }

    // Written as stored (numeric(6,2) and (5,2) come back with their scale), no rounding.
    private static void WriteDecimal(Utf8JsonWriter json, string name, decimal? value)
    {
        if (value is null) json.WriteNull(name);
        else json.WriteNumber(name, value.Value);
    }

    private sealed record ExerciseRow(int Id, int UserId, string Name, bool IsBodyweight, DateTimeOffset CreatedAt);

    private sealed record WorkoutRow(
        int Id, int UserId, DateOnly Date, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
        string? Title, string? Location, string? Notes, decimal? BodyweightKg, DateTimeOffset CreatedAt);

    private sealed record BlockRow(int Id, int WorkoutId, int ExerciseId, int Position);

    private sealed record SetRow(int Id, int WorkoutExerciseId, int SetNumber, decimal? Weight, int Reps, bool IsWarmup);

    // The response side: JSON is serialized into an in-memory buffer, and SendAsync hands
    // the buffer to the client only after a delivery guard passes. At most one batch of
    // rows is ever held in memory.
    private sealed class ChunkedResponse(
        HttpContext http, string connectionString, int userId, int tokenVersion, DateTimeOffset expiresAt,
        LifecycleOptions options, TimeProvider clock)
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(64 * 1024);
        private int _unsentRows;

        // Writes into _buffer; it keeps its nesting state across chunks, so the chunks
        // together form one document.
        private Utf8JsonWriter? _json;
        public Utf8JsonWriter Json => _json ??= new Utf8JsonWriter(_buffer, _writerOptions);

        // One JSON array, fetched batch by batch: `nextBatch(last)` is the query for the
        // rows after `last` (null for the first batch).
        public async Task WriteArrayAsync<TRow>(
            string name, Func<TRow?, IQueryable<TRow>> nextBatch, Action<TRow> writeFields, CancellationToken ct)
            where TRow : class
        {
            Json.WriteStartArray(name);
            TRow? last = null;
            while (true)
            {
                var rows = await nextBatch(last).ToListAsync(ct);
                await WriteRowsAsync(rows, writeFields, ct);
                if (rows.Count < options.ExportBatchSize)
                {
                    break;
                }
                last = rows[^1];
            }
            Json.WriteEndArray();
        }

        // Writes each row as a JSON object, and sends a chunk every ExportBatchSize rows —
        // counted across arrays and queries, so a chunk is always one batch's worth of rows
        // however the queries happen to page.
        public async Task WriteRowsAsync<TRow>(IEnumerable<TRow> rows, Action<TRow> writeFields, CancellationToken ct)
        {
            foreach (var row in rows)
            {
                Json.WriteStartObject();
                writeFields(row);
                Json.WriteEndObject();
                if (++_unsentRows == options.ExportBatchSize)
                {
                    await SendAsync(ct);
                }
            }
        }

        // One chunk: check that the caller may still receive personal data, then write and
        // flush what has been serialized since the last chunk, within the write timeout.
        public async Task SendAsync(CancellationToken ct)
        {
            Json.Flush();
            if (_buffer.WrittenCount == 0)
            {
                return;
            }

            if (clock.GetUtcNow() >= expiresAt)
            {
                throw new ExportAbortedException("token expired", GuardOutcome.Revoked);
            }
            await using (var guard = await OwnConnectionGuard.OpenAsync(connectionString, userId, tokenVersion, options.SharedLockTimeoutMs, ct))
            {
                if (guard.Outcome != GuardOutcome.Ok)
                {
                    throw new ExportAbortedException($"delivery guard {guard.Outcome}", guard.Outcome);
                }
                // Released before the write, as research R4 requires: a client that stops
                // reading must block only its own export, never a deletion (spike A3).
                await guard.CommitAsync();
            }

            if (!http.Response.HasStarted)
            {
                // No response buffering anywhere on this path, so each flush really leaves.
                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "application/json; charset=utf-8";
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{FileName}\"";
            }

            // A cancelled Kestrel write aborts the whole connection, which also fires
            // RequestAborted — the timer's own flag is what identifies the timeout.
            using var writeTimer = new CancellationTokenSource(options.WriteTimeoutMs);
            using var write = CancellationTokenSource.CreateLinkedTokenSource(ct, writeTimer.Token);
            try
            {
                await http.Response.Body.WriteAsync(_buffer.WrittenMemory, write.Token);
                await http.Response.Body.FlushAsync(write.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException && writeTimer.IsCancellationRequested)
            {
                throw new ExportAbortedException("write timeout", null);
            }
            _buffer.ResetWrittenCount();
            _unsentRows = 0;
        }
    }

    // A shared lifecycle guard on its own pooled connection and READ COMMITTED transaction.
    private sealed class OwnConnectionGuard : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private OwnConnectionGuard(NpgsqlConnection connection, NpgsqlTransaction transaction, GuardOutcome outcome)
        {
            _connection = connection;
            _transaction = transaction;
            Outcome = outcome;
        }

        public GuardOutcome Outcome { get; }

        public static async Task<OwnConnectionGuard> OpenAsync(string connectionString, int userId, int tokenVersion, int lockTimeoutMs, CancellationToken ct)
        {
            var connection = new NpgsqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(ct);
                var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var outcome = await AccountLifecycle.AcquireSharedAsync(connection, transaction, userId, tokenVersion, lockTimeoutMs, ct);
                return new OwnConnectionGuard(connection, transaction, outcome);
            }
            catch
            {
                // Not swallowed: rethrown after returning the connection to the pool.
                await connection.DisposeAsync();
                throw;
            }
        }

        // CancellationToken.None: ending a guard that has done its job must not depend on
        // the client happening to disconnect at that moment.
        public Task CommitAsync() => _transaction.CommitAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    // Why a started export stopped. Outcome is the failed guard's, when a guard failed, so
    // an export stopped before its first byte can still answer with a normal status.
    private sealed class ExportAbortedException(string reason, GuardOutcome? outcome) : Exception(reason)
    {
        public GuardOutcome? Outcome { get; } = outcome;
    }
}
