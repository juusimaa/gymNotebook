using System.Security.Cryptography;
using System.Text;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// The sending caps (specs/002 FR-015, FR-016, plan D8): at most PerAddressPerDay emails to
// one address and at most EMAIL_DAILY_CAP in total, each over a rolling 24 hours. Once
// signup is open, anyone can make the app email any address, so without these a script
// could flood a stranger's inbox or burn the provider's daily quota and take password
// reset down for everyone.
//
// The outbox worker calls TryClaimAsync for every message before sending it, so no route
// can forget to. Scoped, like the AppDbContext it writes through.
public sealed class EmailCaps(AppDbContext db, string hashKey, int dailyCap, TimeProvider clock)
{
    // Enough for a real person's signup, a couple of resends and a password reset in one
    // day; too few to flood anyone's inbox.
    public const int PerAddressPerDay = 5;

    private static readonly TimeSpan _window = TimeSpan.FromHours(24);

    // Records one email to `address` and returns true, unless that would go over either
    // cap; then it records nothing and returns false, and the caller drops the message.
    //
    // Delete-count-insert in one transaction. At Postgres's default isolation (read
    // committed) two claims at the same instant can both count below a cap and both
    // insert, so a cap can be passed by one. That is fine for what the caps are for —
    // keeping a script well away from the quota and off one inbox — and avoids a table
    // lock on every email. The transaction makes each claim all-or-nothing, so a failure
    // halfway never leaves the prune done but the row missing, or the other way round.
    public async Task<bool> TryClaimAsync(string address, CancellationToken cancellationToken)
    {
        var recipientHash = HashRecipient(address, hashKey);
        var now = clock.GetUtcNow();
        var since = now - _window;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Pruning here, on the path every email takes, is what keeps rows no longer than
        // 24 hours (FR-016) without a scheduled job. While nothing is sent, nothing is
        // pruned — but then nothing new is being written either, and what's left is only
        // hashes and times.
        await db.EmailSends.Where(s => s.SentAt < since).ExecuteDeleteAsync(cancellationToken);

        // The `>= since` filters repeat what the prune just did, so the counts stay right
        // even if a row with a later-than-now time ever appeared (a clock step back).
        var total = await db.EmailSends.CountAsync(s => s.SentAt >= since, cancellationToken);
        var toRecipient = await db.EmailSends.CountAsync(
            s => s.SentAt >= since && s.RecipientHash == recipientHash, cancellationToken);

        if (total >= dailyCap || toRecipient >= PerAddressPerDay)
        {
            // Commit, not roll back: the prune is still worth keeping.
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        db.EmailSends.Add(new EmailSend { RecipientHash = recipientHash, SentAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Base64 HMAC-SHA256 of the trimmed, lowercased address. A plain SHA-256 would let
    // anyone holding a database copy confirm a guessed address by hashing it; keying with
    // Jwt:Secret means they'd need the signing secret too. Trimmed and lowercased so
    // "Ann@Example.com " and "ann@example.com" share one cap, the same normalization the
    // users table will apply to emails (specs/002 data-model.md).
    public static string HashRecipient(string address, string key)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(mac);
    }
}
