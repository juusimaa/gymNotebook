using System.Data.Common;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymNotebook.Api;

// POST /account/delete (specs/001 user story 4): permanent deletion of the caller's account
// and everything in it, in one transaction.
//
// Coordination (research R4): the deletion takes the account's lifecycle lock *exclusively*,
// in a transaction it owns (never under the shared LifecycleFilter: asking for exclusive
// access while holding shared access deadlocks two callers, analysis I1). So it waits for
// requests already in flight to finish, blocks new ones until it commits, and every request
// after that fails its fresh check with a 401: the old tokens die with the account.
//
// Evidence (research R6, data-model.md → Deletion log lines): three structured log lines
// bracket the transaction, carrying only the account's PrivacyAccountId and the deletion
// boundary. A restore that can't use its preserved pre-restore branch re-deletes accounts
// from these lines, so:
//
//   - deletion.intent      before anything is deleted,
//   - deletion.committed   after the commit, before the success response,
//   - deletion.rolled_back when the transaction definitely didn't commit.
//
// An intent with neither of the others means the outcome is unknown, and the restore
// procedure suspends that account's sign-in instead of guessing. That is exactly what
// happens when the commit itself fails in a way that can't tell whether it applied: the
// response is 503 deletion_outcome_unknown and no second line is written.
//
// This is the only file in the API allowed to remove a User row (research R6 Q2d, guarded
// by AccountIdentityTests): restore reconciliation treats a missing account as a deletion.
public sealed class AccountDeletion(AppDbContext db, LifecycleOptions options, TimeProvider clock, ILogger<AccountDeletion> logger)
{
    // Shown on the completion screen and returned with every successful deletion
    // (contracts/api.md → Deletion response): the one kind of record that is not erased at
    // deletion time, and why it still goes away.
    public const string LogRetentionNotice =
        "Restricted security logs collected before the deletion are not erased early. Each expires within 30 days of when it was originally collected, not 30 days from the deletion.";

    // The backup limit and the deletion-evidence expiry, counted from the boundary. Whole
    // days in UTC, so "30 days" is 30 calendar days whatever the month: AddMonths would
    // give 31 days from 1 July, which is past the limit.
    public const int BackupRetentionDays = 30;
    public const int DeletionEvidenceDays = 31;

    // One template for all three lines, so they stay the same shape for the restore
    // procedure to search. Structured properties, not string concatenation: the console
    // logger prints the message, and a structured sink would keep the three fields apart.
    private const string EvidenceTemplate = "{Event} PrivacyAccountId={PrivacyAccountId} DeletionBoundaryAt={DeletionBoundaryAt:O}";

    public async Task<IResult> RunAsync(DeleteAccountRequest request, HttpContext http)
    {
        // Both checks before anything else: without an explicit confirmation or a
        // password there is nothing to verify, and nothing is touched.
        if (!request.ConfirmDeletion || string.IsNullOrEmpty(request.CurrentPassword))
        {
            return Results.BadRequest(new ErrorResponse("invalid_request"));
        }

        var (userId, tokenVersion) = AccountLifecycle.ReadClaims(http.User);
        var ct = http.RequestAborted;

        // BCrypt is deliberately slow, so the password is checked before the exclusive lock,
        // as in change-password and the export: every other request of this account waits
        // while deletion holds it. The check is still fresh where it matters: a password
        // change in between bumps the token version, which the check under the lock sees
        // (Revoked, 401). AsNoTracking: OnTokenValidated's FindAsync already tracks this
        // User, loaded before any lock.
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PasswordHash, u.PrivacyAccountId })
            .SingleOrDefaultAsync(ct);
        if (account is null)
        {
            return Results.Unauthorized();
        }
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, account.PasswordHash))
        {
            return Results.BadRequest(new ErrorResponse("password_verification_failed"));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Up to ExclusiveLockTimeoutMs (15 s) behind in-flight requests. A timeout is 503
        // temporarily_unavailable: nothing has been deleted, so retrying is safe. The client
        // walking away during the wait (ct) also ends here, with nothing deleted.
        var outcome = await AccountLifecycle.AcquireExclusiveAsync(db, userId, tokenVersion, options.ExclusiveLockTimeoutMs, ct);
        if (outcome != GuardOutcome.Ok)
        {
            return AccountLifecycle.ToResult(outcome, http);
        }

        // From here on, CancellationToken.None: once the deletion has started, whether it
        // commits must not depend on the client happening to close the page (contracts/ui.md:
        // closing the page doesn't cancel a confirmed deletion). The statement timeout
        // below, and lock_timeout for the lock itself, are what bound it instead.
        //
        // The boundary is taken now: after the password, the confirmation and the lock, and
        // just before the intent line (data-model.md). It precedes the commit, so every
        // deadline counted from it is conservative.
        var boundary = clock.GetUtcNow();
        LogEvidence("deletion.intent", account.PrivacyAccountId, boundary);

        try
        {
            // SET LOCAL's parameterized form: the 30 s limit applies to each statement of
            // this transaction only, and dies with it — like the lock_timeout the guard sets,
            // it must never leak to another client of a pooled server connection.
            var statementTimeout = options.DeletionStatementTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {statementTimeout}, true)", CancellationToken.None);

            // Children first (data-model.md → Transactions): workouts cascade to their blocks
            // and sets in the database; exercises can only go after, because blocks reference
            // them with ON DELETE RESTRICT; the User row last. The notice acknowledgement lives
            // on the User row, so it goes with it. Set-based deletes, not loading entities:
            // the reference notebook has 100,000 sets.
            await db.Workouts.Where(w => w.UserId == userId).ExecuteDeleteAsync(CancellationToken.None);
            await db.Exercises.Where(e => e.UserId == userId).ExecuteDeleteAsync(CancellationToken.None);
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (DbException ex)
        {
            // Nothing was committed — COMMIT was never sent — so this is a definite rollback
            // whether the database reported an error or the connection broke: an open
            // transaction on a lost connection is rolled back by the server.
            await transaction.DisposeAsync();
            logger.LogWarning(ex, "Account deletion failed before commit and was rolled back.");
            LogEvidence("deletion.rolled_back", account.PrivacyAccountId, boundary);
            return AccountLifecycle.TemporarilyUnavailable(http);
        }

        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (PostgresException ex)
        {
            // The server answered the COMMIT with an error, so it definitely rolled back.
            logger.LogWarning(ex, "Account deletion commit was refused by the database and rolled back.");
            LogEvidence("deletion.rolled_back", account.PrivacyAccountId, boundary);
            return AccountLifecycle.TemporarilyUnavailable(http);
        }
        catch (DbException ex)
        {
            // Anything else — the connection failed during the COMMIT — can't tell whether
            // it applied. Neither committed nor rolled_back is logged: an intent line alone
            // is what tells the restore procedure the outcome is unknown. The response
            // claims neither success nor rollback (contracts/api.md).
            logger.LogError(ex, "Account deletion commit outcome is unknown.");
            return Results.Json(new ErrorResponse("deletion_outcome_unknown"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        LogEvidence("deletion.committed", account.PrivacyAccountId, boundary);

        // The terminal response is authorized by the completed deletion itself, not by
        // re-checking an account that no longer exists (research R4); it carries nothing
        // personal, only dates.
        return Results.Ok(new DeletionResponse(
            "deleted",
            boundary,
            boundary.AddDays(BackupRetentionDays),
            boundary.AddDays(DeletionEvidenceDays),
            LogRetentionNotice));
    }

    // Only the event, the UUID and the boundary: no integer user id, username, token or
    // notebook content (data-model.md). Information level; appsettings.json pins this
    // category to Information so a stricter default can't silently drop the evidence.
    private void LogEvidence(string eventName, Guid privacyAccountId, DateTimeOffset boundary) =>
        logger.LogInformation(EvidenceTemplate, eventName, privacyAccountId, boundary.ToUniversalTime());
}
