using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// What PasswordReplacement.ReplaceAsync did. Each caller maps it to its own answer: the
// same race means "your session ended" (401) to change-password, which has a session,
// and "this link no longer works" (400 invalid) to a password reset, which doesn't.
public enum PasswordReplacementOutcome
{
    // The new hash is stored, TokenVersion is expectedTokenVersion + 1, and that version
    // was still current when checked afterwards: safe to hand out a token for it.
    Replaced,

    // Nothing changed: under the lock, the account was gone or its token version had
    // already moved past the expected one (another password change, reset or deletion).
    Revoked,

    // Nothing changed: the lock wait or the update hit a timeout or another database
    // failure. Safe to retry.
    Busy,

    // The change committed, but by the delivery check a deletion or another password
    // change had already followed it, so the new version is stale and no token may go out.
    Superseded,
}

// Sets an account's password and revokes every session in one step: the locked write
// shared by POST /auth/change-password and POST /auth/password-reset/confirm (specs/002
// plan D3), so the two can't drift apart in how they coordinate with deletion.
//
// Revoking every token is an account-lifecycle operation, so this takes *exclusive*
// access (specs/001 research R4) in a transaction it owns. Callers must not run it under
// the shared-access LifecycleFilter: holding shared access while asking for exclusive
// would be a lock upgrade, which deadlocks two concurrent callers (analysis I1).
public static class PasswordReplacement
{
    // `newHash` is computed by the caller *before* this runs: BCrypt is deliberately slow,
    // and doing it outside the lock keeps the time every other request of the account
    // waits short. A concurrent change in between shows up under the lock as a
    // token-version mismatch (Revoked).
    //
    // `confirmEmailAt`, when given, stamps EmailVerifiedAt if it is still empty, in the
    // same UPDATE: a completed reset proves the inbox, so it confirms the address
    // (specs/002 FR-008). An address already confirmed keeps its original time.
    public static async Task<PasswordReplacementOutcome> ReplaceAsync(
        AppDbContext db, int userId, int expectedTokenVersion, string newHash, DateTimeOffset? confirmEmailAt,
        LifecycleOptions lifecycle, CancellationToken ct)
    {
        var newVersion = expectedTokenVersion + 1;

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var outcome = await AccountLifecycle.AcquireExclusiveAsync(db, userId, expectedTokenVersion, lifecycle.ExclusiveLockTimeoutMs, ct);
            if (outcome != GuardOutcome.Ok)
            {
                return outcome == GuardOutcome.TimedOut ? PasswordReplacementOutcome.Busy : PasswordReplacementOutcome.Revoked;
            }

            // The new hash and the version bump land together or not at all. The bump is
            // what revokes every token issued so far (see PLAN.md, Auth section) — and
            // every reset link, whose "tv" is now stale.
            //
            // A database failure here — including one EF's execution strategy wraps, such
            // as a lock_timeout on the user's row (T099) — is before COMMIT, so nothing
            // changed and the old password and tokens still stand: Busy, safe to retry,
            // rather than an unhandled 500.
            try
            {
                // The block-bodied setter (EF Core 10) lets the confirmation stamp be added
                // only when asked for. `?? stamp` keeps an earlier confirmation time.
                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s =>
                    {
                        s.SetProperty(u => u.PasswordHash, newHash);
                        s.SetProperty(u => u.TokenVersion, newVersion);
                        if (confirmEmailAt is { } stamp)
                        {
                            s.SetProperty(u => u.EmailVerifiedAt, u => u.EmailVerifiedAt ?? stamp);
                        }
                    }, ct);
            }
            catch (Exception ex) when (AccountLifecycle.IsDatabaseFailure(ex))
            {
                return PasswordReplacementOutcome.Busy;
            }
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Delivery guard, only after the exclusive transaction has fully ended (so shared
        // access is never requested while exclusive is held). If a deletion or another
        // password change won in the meantime, the new version is no longer current and
        // the caller must not emit a token for it (spike A4). Shared access requires a
        // confirmed address; a reset has just stamped it, and change-password's caller was
        // already confirmed.
        await using (var delivery = await db.Database.BeginTransactionAsync(ct))
        {
            var outcome = await AccountLifecycle.AcquireSharedAsync(db, userId, newVersion, lifecycle.SharedLockTimeoutMs, ct);
            if (outcome != GuardOutcome.Ok)
            {
                return PasswordReplacementOutcome.Superseded;
            }
            await delivery.CommitAsync(CancellationToken.None);
        }

        return PasswordReplacementOutcome.Replaced;
    }
}
