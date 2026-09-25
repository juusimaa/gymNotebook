using System.Collections.Concurrent;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace GymNotebook.Api;

// =======================================================================================
// SPIKE — T008, research R4 → Validation spike Part A. Throwaway code on the
// spike/r4-cancellation branch; never merged. It prototypes just enough of the R4 design
// (shared/exclusive advisory-lock guards, the endpoint filter, a fake account deletion,
// a fake chunked export and a guarded password change) for the A1–A6 tests in
// GymNotebook.Tests/SpikeR4/ to measure. Everything is off unless Spike:R4:Enabled is set,
// and the filter is attached only when Spike:R4:Guard is set too.
// =======================================================================================

// How the shared guard combines "take the lock" with "check the account" (scenario A6).
public enum LockCheckVariant
{
    // One statement: SELECT lock(...), (SELECT token_version ...). Under READ COMMITTED the
    // statement's snapshot is taken before the lock wait, so it may see a deleted user.
    SingleStatement,

    // Two round trips: lock, then a separate SELECT with its own, later snapshot.
    TwoStatements,

    // The same two statements sent together in one NpgsqlBatch (one round trip).
    Batch,
}

public sealed class SpikeR4Options
{
    public bool Enabled { get; set; }
    public bool Guard { get; set; }
    public LockCheckVariant Variant { get; set; } = LockCheckVariant.TwoStatements;
    public int SharedLockTimeoutMs { get; set; } = 5000;
    public int ExclusiveLockTimeoutMs { get; set; } = 15000;
    public int WriteTimeoutMs { get; set; } = 10000;
}

public enum GuardOutcome
{
    Ok,
    Revoked,
    TimedOut,
}

// Named pause points the tests hook into to force exact interleavings (barriers, not
// sleeps), plus an event log the tests read to learn *why* something ended. Registered as
// a singleton; the two-host fixture shares one instance between both hosts.
public sealed class SpikeHooks
{
    private readonly ConcurrentDictionary<string, Func<int, Task>> _handlers = new();

    public ConcurrentQueue<(string Name, string Detail)> Events { get; } = new();

    public void On(string name, Func<int, Task> handler) => _handlers[name] = handler;

    public Task At(string name, int index = 0) =>
        _handlers.TryGetValue(name, out var handler) ? handler(index) : Task.CompletedTask;

    public void Report(string name, string detail) => Events.Enqueue((name, detail));
}

public static class LifecycleLocks
{
    // First key of the two-int advisory lock: a namespace so these locks can never collide
    // with any other advisory lock the app takes later (R3's export-limit lock has its own).
    public const int Namespace = 0x4C494645; // "LIFE"

    // lock_not_available: what a lock wait raises when lock_timeout expires.
    private const string LockNotAvailable = "55P03";

    // SET LOCAL, never SET: production goes through Neon's transaction-mode pooler, where a
    // session-level SET would leak to whichever client gets this server connection next.
    // SET can't take a bind parameter, which is fine for an int.
    public static async Task SetLockTimeoutAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int milliseconds, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL lock_timeout = {milliseconds}", connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    // Takes shared access for userId and then checks, freshly, that the account exists and
    // its token_version still equals the caller's "tv" claim.
    public static async Task<GuardOutcome> SharedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int userId, int tokenVersion,
        LockCheckVariant variant, int lockTimeoutMs, CancellationToken ct)
    {
        try
        {
            await SetLockTimeoutAsync(connection, transaction, lockTimeoutMs, ct);
            var current = variant switch
            {
                LockCheckVariant.SingleStatement => await SingleStatementAsync(connection, transaction, userId, ct),
                LockCheckVariant.TwoStatements => await TwoStatementsAsync(connection, transaction, "pg_advisory_xact_lock_shared", userId, ct),
                LockCheckVariant.Batch => await BatchAsync(connection, transaction, userId, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
            return current == tokenVersion ? GuardOutcome.Ok : GuardOutcome.Revoked;
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            return GuardOutcome.TimedOut;
        }
    }

    // Exclusive access for deletion and password change. Always two statements: whatever A6
    // concludes for the shared guard, the exclusive side has no reason to risk it.
    public static async Task<GuardOutcome> ExclusiveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int userId, int tokenVersion,
        int lockTimeoutMs, CancellationToken ct)
    {
        try
        {
            await SetLockTimeoutAsync(connection, transaction, lockTimeoutMs, ct);
            var current = await TwoStatementsAsync(connection, transaction, "pg_advisory_xact_lock", userId, ct);
            return current == tokenVersion ? GuardOutcome.Ok : GuardOutcome.Revoked;
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            return GuardOutcome.TimedOut;
        }
    }

    private static async Task<int?> SingleStatementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int userId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(@ns, @id)::text, (SELECT token_version FROM users WHERE id = @id)",
            connection, transaction);
        command.Parameters.AddWithValue("ns", Namespace);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.IsDBNull(1) ? null : reader.GetInt32(1);
    }

    private static async Task<int?> TwoStatementsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string lockFunction, int userId, CancellationToken ct)
    {
        await using (var lockCommand = new NpgsqlCommand($"SELECT {lockFunction}(@ns, @id)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("ns", Namespace);
            lockCommand.Parameters.AddWithValue("id", userId);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using var checkCommand = new NpgsqlCommand("SELECT token_version FROM users WHERE id = @id", connection, transaction);
        checkCommand.Parameters.AddWithValue("id", userId);
        return (int?)await checkCommand.ExecuteScalarAsync(ct);
    }

    private static async Task<int?> BatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int userId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);
        var lockCommand = new NpgsqlBatchCommand("SELECT pg_advisory_xact_lock_shared(@ns, @id)");
        lockCommand.Parameters.AddWithValue("ns", Namespace);
        lockCommand.Parameters.AddWithValue("id", userId);
        var checkCommand = new NpgsqlBatchCommand("SELECT token_version FROM users WHERE id = @id");
        checkCommand.Parameters.AddWithValue("id", userId);
        batch.BatchCommands.Add(lockCommand);
        batch.BatchCommands.Add(checkCommand);

        await using var reader = await batch.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct); // skip the lock's void result
        return await reader.ReadAsync(ct) ? reader.GetInt32(0) : null;
    }

    // The raw Npgsql objects behind EF's current transaction, so the guard's statements run
    // on the same connection and inside the same transaction as the handler's queries.
    public static (NpgsqlConnection, NpgsqlTransaction) Unwrap(AppDbContext db, IDbContextTransaction transaction) =>
        ((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());

    public static (int UserId, int TokenVersion) ReadClaims(ClaimsPrincipal user) =>
        (int.Parse(user.FindFirst(JwtRegisteredClaimNames.Sub)!.Value), int.Parse(user.FindFirst("tv")!.Value));

    public static IResult Map(GuardOutcome outcome, HttpContext http)
    {
        if (outcome == GuardOutcome.TimedOut)
        {
            http.Response.Headers.RetryAfter = "1";
            return Results.Json(new { code = "temporarily_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Unauthorized();
    }
}

// The prototype of R4 → Q3's endpoint filter. Reads write their response while still
// holding shared access, then commit. Writes commit first, then deliver under a fresh
// "delivery guard", so a 2xx is never sent for work that didn't commit and no personal
// bytes are sent after a deletion or revocation wins.
public sealed class LifecycleFilter(SpikeR4Options options, SpikeHooks hooks) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var (userId, tokenVersion) = LifecycleLocks.ReadClaims(http.User);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (connection, npgsqlTransaction) = LifecycleLocks.Unwrap(db, transaction);
        var outcome = await LifecycleLocks.SharedAsync(connection, npgsqlTransaction, userId, tokenVersion, options.Variant, options.SharedLockTimeoutMs, ct);
        if (outcome != GuardOutcome.Ok)
        {
            return LifecycleLocks.Map(outcome, http);
        }
        await hooks.At("filter.locked");

        var result = await next(context);

        // Finding for A5: a handler can return a 4xx after intermediate SaveChangesAsync
        // calls (PUT /workouts/{id}/exercises returns 400 mid-loop after deleting the old
        // blocks). Its own transaction used to roll that back; now the filter owns the
        // transaction, so it must commit only on success and let disposal roll back the rest.
        if (result is not IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } || result is not IResult httpResult)
        {
            return result;
        }

        if (HttpMethods.IsGet(http.Request.Method))
        {
            await WriteBoundedAsync(http, httpResult);
            await transaction.CommitAsync(CancellationToken.None);
            return Results.Empty;
        }

        await transaction.CommitAsync(ct);
        await hooks.At("filter.committed");

        await using var delivery = await db.Database.BeginTransactionAsync(ct);
        var (deliveryConnection, deliveryTransaction) = LifecycleLocks.Unwrap(db, delivery);
        outcome = await LifecycleLocks.SharedAsync(deliveryConnection, deliveryTransaction, userId, tokenVersion, options.Variant, options.SharedLockTimeoutMs, ct);
        if (outcome != GuardOutcome.Ok)
        {
            // Q5: a write that committed but whose delivery guard fails gets 401, no body.
            return Results.Unauthorized();
        }

        await WriteBoundedAsync(http, httpResult);
        await delivery.CommitAsync(CancellationToken.None);
        return Results.Empty;
    }

    // Writes and completes the response inside the write timeout. On timeout the connection
    // is aborted, which ends the write and lets the transaction (and its lock) go.
    private async Task WriteBoundedAsync(HttpContext http, IResult result)
    {
        using var timeout = new CancellationTokenSource(options.WriteTimeoutMs);
        await using var registration = timeout.Token.Register(http.Abort);
        await result.ExecuteAsync(http);
        await http.Response.CompleteAsync();
    }
}

public static class SpikeR4Endpoints
{
    public static void Map(WebApplication app, string jwtSecret, int jwtExpiryMinutes)
    {
        var spike = app.MapGroup("/spike").RequireAuthorization();

        // Fake account deletion: exclusive access, R5's delete order, one transaction.
        spike.MapPost("/account/delete", async (ClaimsPrincipal caller, AppDbContext db, SpikeR4Options options, SpikeHooks hooks, HttpContext http) =>
        {
            var ct = http.RequestAborted;
            var (userId, tokenVersion) = LifecycleLocks.ReadClaims(caller);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var (connection, npgsqlTransaction) = LifecycleLocks.Unwrap(db, transaction);
            var outcome = await LifecycleLocks.ExclusiveAsync(connection, npgsqlTransaction, userId, tokenVersion, options.ExclusiveLockTimeoutMs, ct);
            if (outcome != GuardOutcome.Ok)
            {
                hooks.Report("delete.refused", outcome.ToString());
                return LifecycleLocks.Map(outcome, http);
            }
            hooks.Report("delete.locked", DateTimeOffset.UtcNow.ToString("O"));
            await hooks.At("delete.locked");

            // Workouts cascade to blocks and sets; exercises only after, because blocks
            // reference exercises with ON DELETE RESTRICT.
            await db.Workouts.Where(w => w.UserId == userId).ExecuteDeleteAsync(ct);
            await db.Exercises.Where(e => e.UserId == userId).ExecuteDeleteAsync(ct);
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

            await hooks.At("delete.beforeCommit");
            await transaction.CommitAsync(ct);
            hooks.Report("delete.committed", DateTimeOffset.UtcNow.ToString("O"));
            return Results.Ok(new { deleted = true });
        });

        // Guarded password change (R4, analysis I1): exclusive transaction, commit, then
        // deliver the new token only if a fresh shared guard still accepts the *new* version.
        spike.MapPost("/auth/change-password", async (ChangePasswordRequest request, ClaimsPrincipal caller, AppDbContext db, SpikeR4Options options, SpikeHooks hooks, HttpContext http) =>
        {
            var ct = http.RequestAborted;
            var (userId, tokenVersion) = LifecycleLocks.ReadClaims(caller);

            // BCrypt is deliberately slow (~100+ ms), so verify and hash *before* taking the
            // exclusive lock; the lock section only re-checks token_version and writes. A
            // concurrent change in between shows up as a version mismatch under the lock.
            // AsNoTracking: OnTokenValidated's FindAsync already tracks this User, and a
            // tracked query would hand back that (possibly stale) instance's values.
            var hash = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleOrDefaultAsync(ct);
            if (hash is null || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, hash))
            {
                return Results.Unauthorized();
            }
            var newHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            var newVersion = tokenVersion + 1;

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                var (connection, npgsqlTransaction) = LifecycleLocks.Unwrap(db, transaction);
                var outcome = await LifecycleLocks.ExclusiveAsync(connection, npgsqlTransaction, userId, tokenVersion, options.ExclusiveLockTimeoutMs, ct);
                if (outcome != GuardOutcome.Ok)
                {
                    return LifecycleLocks.Map(outcome, http);
                }
                await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PasswordHash, newHash)
                    .SetProperty(u => u.TokenVersion, newVersion), ct);
                await transaction.CommitAsync(ct);
            }
            // The exclusive transaction is fully closed here: the shared guard below is never
            // requested while exclusive access is still held (no upgrade, no self-deadlock).
            await hooks.At("pw.committed");

            await using var delivery = await db.Database.BeginTransactionAsync(ct);
            var (deliveryConnection, deliveryTransaction) = LifecycleLocks.Unwrap(db, delivery);
            var deliveryOutcome = await LifecycleLocks.SharedAsync(deliveryConnection, deliveryTransaction, userId, newVersion, options.Variant, options.SharedLockTimeoutMs, ct);
            if (deliveryOutcome != GuardOutcome.Ok)
            {
                hooks.Report("pw.suppressed", deliveryOutcome.ToString());
                return Results.Unauthorized();
            }
            await delivery.CommitAsync(ct);

            var token = JwtTokenFactory.CreateToken(
                new User { Id = userId, Username = "", PasswordHash = "", TokenVersion = newVersion }, jwtSecret, jwtExpiryMinutes);
            return Results.Ok(new AuthResponse(token));
        });

        // Fake export shaped like R3/R4: short init guard on its own connection, then a
        // REPEATABLE READ snapshot that never holds the lifecycle lock, then per chunk a
        // short delivery guard on yet another connection before the bytes are handed over.
        spike.MapGet("/export", async (int chunks, int chunkBytes, ClaimsPrincipal caller, AppDbContext db, SpikeR4Options options, SpikeHooks hooks, HttpContext http) =>
        {
            var ct = http.RequestAborted;
            var (userId, tokenVersion) = LifecycleLocks.ReadClaims(caller);
            var connectionString = db.Database.GetConnectionString()!;

            var initOutcome = await GuardOnOwnConnectionAsync(connectionString, userId, tokenVersion, options, hooks, ct);
            if (initOutcome != GuardOutcome.Ok)
            {
                return LifecycleLocks.Map(initOutcome, http);
            }

            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Workouts.Where(w => w.UserId == userId).CountAsync(ct); // pins the snapshot

            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.ContentType = "application/json";
            await http.Response.StartAsync(ct);
            var body = http.Response.Body;
            var chunk = Encoding.UTF8.GetBytes(new string('x', chunkBytes));

            await body.WriteAsync("[\""u8.ToArray(), ct);
            for (var i = 0; i < chunks; i++)
            {
                await hooks.At("export.beforeGuard", i);
                var outcome = await GuardOnOwnConnectionAsync(connectionString, userId, tokenVersion, options, hooks, ct);
                if (outcome != GuardOutcome.Ok)
                {
                    hooks.Report("export.aborted", $"guard:{outcome} before chunk {i}");
                    http.Abort();
                    return Results.Empty;
                }

                var ended = await WriteChunkAsync(http, body, chunk, options.WriteTimeoutMs, ct);
                if (ended is not null)
                {
                    hooks.Report("export.aborted", $"{ended} at chunk {i}");
                    http.Abort();
                    return Results.Empty;
                }
                hooks.Report("export.chunkSent", i.ToString());
                await hooks.At("export.chunkSent", i);
            }

            // The closing bytes go out only after a final check, so a stream cut anywhere
            // earlier can never parse as a complete document.
            var finalOutcome = await GuardOnOwnConnectionAsync(connectionString, userId, tokenVersion, options, hooks, ct);
            if (finalOutcome != GuardOutcome.Ok)
            {
                hooks.Report("export.aborted", $"guard:{finalOutcome} before closing");
                http.Abort();
                return Results.Empty;
            }
            await body.WriteAsync("\"]"u8.ToArray(), ct);
            await snapshot.CommitAsync(ct);
            hooks.Report("export.completed", chunks.ToString());
            return Results.Empty;
        });
    }

    private static async Task<GuardOutcome> GuardOnOwnConnectionAsync(string connectionString, int userId, int tokenVersion, SpikeR4Options options, SpikeHooks hooks, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var outcome = await LifecycleLocks.SharedAsync(connection, transaction, userId, tokenVersion, options.Variant, options.SharedLockTimeoutMs, ct);
        if (outcome == GuardOutcome.Ok)
        {
            await hooks.At("export.guardHeld");
            await transaction.CommitAsync(ct);
        }
        return outcome;
    }

    // Returns null on success, or why the write ended. The write timeout bounds how long a
    // client that stops reading can keep this request alive.
    private static async Task<string?> WriteChunkAsync(HttpContext http, Stream body, byte[] chunk, int writeTimeoutMs, CancellationToken requestAborted)
    {
        // Finding: cancelling a Kestrel write aborts the whole connection, so RequestAborted
        // fires too — the timer's own flag is what tells a timeout from a client abort.
        using var timer = new CancellationTokenSource(writeTimeoutMs);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, timer.Token);
        try
        {
            await body.WriteAsync(chunk, timeout.Token);
            await body.FlushAsync(timeout.Token);
            return null;
        }
        catch (Exception) when (timer.IsCancellationRequested)
        {
            return "write-timeout";
        }
        catch (Exception ex) when (requestAborted.IsCancellationRequested || ex is IOException)
        {
            // Kestrel itself can abort a stalled response (MinResponseDataRate) before our
            // timeout fires; the tests record which one ended the stream.
            return $"connection-aborted:{ex.GetType().Name}";
        }
    }
}
