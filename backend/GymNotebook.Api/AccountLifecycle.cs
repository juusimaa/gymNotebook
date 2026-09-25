using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace GymNotebook.Api;

// Account-lifecycle coordination (specs/001 research R4). The problem: a JWT is checked
// once, when a request arrives, but account deletion and password changes can commit
// while that request is still running — and then a handler would answer for an account
// that no longer exists, or a revoked token would still receive personal data.
//
// The fix is a PostgreSQL advisory lock per account, keyed by User.Id:
//
//   - Ordinary authenticated requests take it *shared* (many at once), through
//     LifecycleFilter below, and re-check the account and token version under it.
//   - Deletion and password change take it *exclusive* (AcquireExclusiveAsync), so they
//     wait for in-flight requests to finish and block new ones until they commit.
//
// Transaction-level locks (pg_advisory_xact_lock*) rather than session-level ones: they
// release automatically when the transaction ends, and they stay correct through Neon's
// transaction-mode connection pooler, where a session lock could outlive the request on a
// pooled server connection. Database-wide locks, rather than anything in process, because
// two app replicas must see each other's locks.

// Lock and write timeouts, bound from the "Lifecycle" configuration section. The defaults
// are research R4's Q4 values, which spike Part A kept unchanged; tests shorten them.
public sealed class LifecycleOptions
{
    // How long an ordinary request waits for shared access (i.e. behind a deletion or
    // password change holding exclusive access) before giving up with 503.
    public int SharedLockTimeoutMs { get; set; } = 5000;

    // How long a guarded response write may take. This is the only bound on how long a
    // client that stops reading can hold shared access (spike A3), so it must stay below
    // ExclusiveLockTimeoutMs or a stalled client could make every deletion time out.
    public int WriteTimeoutMs { get; set; } = 10000;

    // How long deletion and password change wait for exclusive access.
    public int ExclusiveLockTimeoutMs { get; set; } = 15000;

    // Rows per keyset batch in the export; each batch is one chunk with its own delivery
    // guard, so this is also how far an export can get past a deletion (at most one chunk).
    public int ExportBatchSize { get; set; } = 1000;

    // The export's hard cap. SC-003's target is 60 s; this bounds how long the snapshot
    // transaction can stay open whatever the client or the database does.
    public int ExportMaxDurationMs { get; set; } = 120000;

    public void Validate()
    {
        if (WriteTimeoutMs >= ExclusiveLockTimeoutMs)
        {
            throw new InvalidOperationException(
                "Lifecycle:WriteTimeoutMs must be below Lifecycle:ExclusiveLockTimeoutMs, or a stalled response write could outlast every deletion's lock wait.");
        }
        if (ExportBatchSize <= 0 || ExportMaxDurationMs <= 0)
        {
            throw new InvalidOperationException("Lifecycle:ExportBatchSize and Lifecycle:ExportMaxDurationMs must be positive.");
        }
    }
}

public enum GuardOutcome
{
    // The lock is held and the account still exists with the caller's token version.
    Ok,

    // The account is gone or the token version moved on (deleted, or password changed).
    Revoked,

    // The lock wait hit its timeout; nothing was done, so retrying is safe.
    TimedOut,
}

// Endpoint metadata marking a route as running under LifecycleFilter. Added together with
// the filter by RequireAccountLifecycle(), so LifecycleCoverageTests can check from the
// endpoint list alone that no authorized route was left out.
public sealed class AccountLifecycleGuardMetadata;

public static class AccountLifecycle
{
    // First key of the two-int advisory lock: a namespace, so these locks can never collide
    // with any other advisory lock the app takes (the export limit has its own,
    // NotebookExport.ExportLockNamespace).
    // The value spells "LIFE" in ASCII, which makes it recognizable in pg_locks.
    public const int LockNamespace = 0x4C494645;

    // Retry-After on a 503: a deletion or password change holds exclusive access for a
    // few seconds at most, so a short wait is enough.
    private const string RetryAfterSeconds = "5";

    // lock_not_available: what a lock wait raises when lock_timeout expires.
    private const string LockNotAvailable = "55P03";

    // Attaches the shared-access filter to a route or route group, with the marker metadata
    // the coverage test looks for, and documents the 503 every guarded route can return.
    public static TBuilder RequireAccountLifecycle<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter<TBuilder, LifecycleFilter>();
        builder.WithMetadata(
            new AccountLifecycleGuardMetadata(),
            new ProducesResponseTypeMetadata(StatusCodes.Status503ServiceUnavailable, typeof(ErrorResponse), ["application/json"]));
        return builder;
    }

    // Shared access for an ordinary request, or for a delivery guard.
    public static Task<GuardOutcome> AcquireSharedAsync(AppDbContext db, int userId, int tokenVersion, int lockTimeoutMs, CancellationToken ct) =>
        AcquireAsync(db, "pg_advisory_xact_lock_shared", userId, tokenVersion, lockTimeoutMs, ct);

    // Exclusive access for the operations that end or revoke an account's access: password
    // change now, account deletion in US4. Those endpoints own their transaction and never
    // run under LifecycleFilter: asking for exclusive access while already holding shared
    // access would be a lock upgrade, and two concurrent upgraders deadlock (analysis I1).
    public static Task<GuardOutcome> AcquireExclusiveAsync(AppDbContext db, int userId, int tokenVersion, int lockTimeoutMs, CancellationToken ct) =>
        AcquireAsync(db, "pg_advisory_xact_lock", userId, tokenVersion, lockTimeoutMs, ct);

    // Shared access on a connection the caller opened itself, outside any AppDbContext.
    // The export needs this (research R3/R4): its snapshot transaction occupies the
    // request's context for the whole download, so its initialization and per-chunk
    // delivery guards run on separate, short READ COMMITTED transactions.
    public static Task<GuardOutcome> AcquireSharedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int userId, int tokenVersion, int lockTimeoutMs, CancellationToken ct) =>
        AcquireAsync(connection, transaction, "pg_advisory_xact_lock_shared", userId, tokenVersion, lockTimeoutMs, ct);

    private static Task<GuardOutcome> AcquireAsync(AppDbContext db, string lockFunction, int userId, int tokenVersion, int lockTimeoutMs, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The lifecycle guard must run inside a transaction: its lock is released when that transaction ends.");

        // The raw Npgsql objects behind EF's transaction, so the guard runs on the same
        // connection and in the same transaction as the handler's own queries.
        return AcquireAsync(
            (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction(),
            lockFunction, userId, tokenVersion, lockTimeoutMs, ct);
    }

    // Takes the lock inside the given transaction, then freshly reads the account's token
    // version. Everything is sent as one NpgsqlBatch (one round trip), but as *separate
    // statements*: under READ COMMITTED each statement reads from a snapshot taken when it
    // starts, so a single "lock and check" statement that waited on the lock while a
    // deletion committed would still see the deleted user and pass (spike A6). The
    // separate check statement starts after the lock is granted and sees the deletion.
    private static async Task<GuardOutcome> AcquireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string lockFunction, int userId, int tokenVersion, int lockTimeoutMs, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);

        // SET LOCAL, never SET: production goes through Neon's transaction-mode pooler,
        // where a session-level SET would leak to whichever client gets this server
        // connection next. SET can't take a bind parameter, which is fine for an int.
        batch.BatchCommands.Add(new NpgsqlBatchCommand($"SET LOCAL lock_timeout = {lockTimeoutMs}"));

        var lockCommand = new NpgsqlBatchCommand($"SELECT {lockFunction}(@ns, @id)");
        lockCommand.Parameters.AddWithValue("ns", LockNamespace);
        lockCommand.Parameters.AddWithValue("id", userId);
        batch.BatchCommands.Add(lockCommand);

        var checkCommand = new NpgsqlBatchCommand("SELECT token_version FROM users WHERE id = @id");
        checkCommand.Parameters.AddWithValue("id", userId);
        batch.BatchCommands.Add(checkCommand);

        try
        {
            await using var reader = await batch.ExecuteReaderAsync(ct);

            // Step through the batch's result sets to the check's. Matching on the column
            // name keeps this independent of whether SET produces an (empty) result set.
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0) == "token_version")
                {
                    var current = await reader.ReadAsync(ct) ? reader.GetInt32(0) : (int?)null;
                    return current == tokenVersion ? GuardOutcome.Ok : GuardOutcome.Revoked;
                }
            }
            while (await reader.NextResultAsync(ct));

            throw new InvalidOperationException("The lifecycle guard's check returned no result set.");
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            // The transaction is now aborted; the caller's disposal rolls it back.
            return GuardOutcome.TimedOut;
        }
    }

    // The user id and token version the bearer handler already validated. A failure here
    // is a pipeline bug (OnTokenValidated rejects tokens without both), hence the throw.
    public static (int UserId, int TokenVersion) ReadClaims(ClaimsPrincipal user)
    {
        if (!int.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)
            || !int.TryParse(user.FindFirst("tv")?.Value, out var tokenVersion))
        {
            throw new InvalidOperationException("Authenticated user has no valid sub/tv claims.");
        }
        return (userId, tokenVersion);
    }

    // research R4 → Q5. A timeout is 503 "temporarily_unavailable" with Retry-After:
    // nothing was changed, so retrying is safe. A revoked account is the same bare 401 as
    // any revoked token, so no new status reveals that an account was deleted.
    public static IResult ToResult(GuardOutcome outcome, HttpContext http)
    {
        switch (outcome)
        {
            case GuardOutcome.TimedOut:
                http.Response.Headers.RetryAfter = RetryAfterSeconds;
                return Results.Json(new ErrorResponse("temporarily_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
            case GuardOutcome.Revoked:
                return Results.Unauthorized();
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Only a failed guard maps to an error result.");
        }
    }
}

// research R4 → Q3: the endpoint filter on every route that needs only shared access.
// It opens a transaction on the request's scoped AppDbContext — the same instance the
// handler receives, so the handler's queries run inside it — takes shared access and
// re-checks the account before the handler runs. Then:
//
//   - Reads write their response while still holding the lock, then commit: no personal
//     bytes leave after a deletion could have committed.
//   - Writes commit first, then write the response under a fresh *delivery guard* (a new
//     transaction, a new shared lock, a new check). Two steps because a 2xx must never be
//     sent for work that didn't commit, and the response must not be sent if a deletion
//     or password change won in between. The account lock is always taken before any
//     notebook row is touched, so lock order is the same everywhere.
//
// Only a 2xx commits. Any other result — a 400 after intermediate saves, a 404 — returns
// without committing, and disposing the transaction rolls back whatever the handler
// saved. An exception does the same.
//
// Each guarded request holds one pooled database connection for its whole duration,
// including the response write, bounded by LifecycleOptions.WriteTimeoutMs.
public sealed class LifecycleFilter(LifecycleOptions options, ILogger<LifecycleFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var (userId, tokenVersion) = AccountLifecycle.ReadClaims(http.User);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var outcome = await AccountLifecycle.AcquireSharedAsync(db, userId, tokenVersion, options.SharedLockTimeoutMs, ct);
        if (outcome != GuardOutcome.Ok)
        {
            return AccountLifecycle.ToResult(outcome, http);
        }

        var result = await next(context);

        // Every handler in this API returns an IResult; anything else would bypass the
        // commit-only-on-success rule below, so treat it as the bug it would be.
        if (result is not IResult httpResult)
        {
            throw new InvalidOperationException("Endpoints under the lifecycle filter must return an IResult.");
        }

        // A result without an explicit status (Results.Json with none given) writes 200.
        var status = (httpResult as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        if (status is < 200 or >= 300)
        {
            return httpResult;
        }

        if (HttpMethods.IsGet(http.Request.Method))
        {
            await WriteBoundedAsync(http, httpResult);
            // CancellationToken.None: the response is already out; ending the read-only
            // transaction cleanly is all that's left, whatever the client does.
            await transaction.CommitAsync(CancellationToken.None);
            return Results.Empty;
        }

        // None here too: once the handler has run, whether the write commits must not
        // depend on the client happening to disconnect at this moment.
        await transaction.CommitAsync(CancellationToken.None);

        await using var delivery = await db.Database.BeginTransactionAsync(ct);
        outcome = await AccountLifecycle.AcquireSharedAsync(db, userId, tokenVersion, options.SharedLockTimeoutMs, ct);
        if (outcome != GuardOutcome.Ok)
        {
            // Q5: the write committed, but the account was deleted or the token revoked
            // first, so no personal bytes are sent — 401 with no body. Also for a timeout:
            // 503 would claim nothing changed, which is false once the write committed.
            return Results.Unauthorized();
        }

        await WriteBoundedAsync(http, httpResult);
        await delivery.CommitAsync(CancellationToken.None);
        return Results.Empty;
    }

    // Writes and completes the response within the write timeout. On timeout the whole
    // connection is aborted — Kestrel can't cancel just one write — which ends the write
    // and lets the caller release its transaction and lock.
    private async Task WriteBoundedAsync(HttpContext http, IResult result)
    {
        using var timeout = new CancellationTokenSource(options.WriteTimeoutMs);
        await using var abortOnTimeout = timeout.Token.Register(http.Abort);
        try
        {
            await result.ExecuteAsync(http);
            await http.Response.CompleteAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
                                   && (timeout.IsCancellationRequested || http.RequestAborted.IsCancellationRequested))
        {
            // The connection is gone (our timeout or the client), so nothing more can be
            // delivered. Aborting also fires RequestAborted, so the timer's own token is
            // what tells a stalled client apart from one that disconnected (spike finding).
            logger.LogInformation("Guarded response write abandoned: {Reason}.",
                timeout.IsCancellationRequested ? "write timeout" : "client disconnected");
        }
    }
}
