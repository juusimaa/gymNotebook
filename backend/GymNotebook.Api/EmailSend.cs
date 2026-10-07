namespace GymNotebook.Api;

// The email_sends table (specs/002 data-model.md): one row per email the app sent or tried
// to send in the last 24 hours, kept only so EmailCaps can count them. It deliberately
// holds nothing that identifies a person — no address, no user id, no foreign key to
// users — because signup emails to addresses that have no account count towards the caps
// too, and because a database read should reveal nobody's inbox.
public class EmailSend
{
    // bigint identity: rows come and go every day, so the sequence outgrows an int sooner
    // than any other table's would, even though only a day's worth exist at any time.
    public long Id { get; set; }

    // Base64 HMAC-SHA256 of the lowercased address, keyed with Jwt:Secret (EmailCaps).
    // Equal addresses give equal hashes, which is all counting needs; without the key the
    // hash can't be reversed by trying candidate addresses.
    public required string RecipientHash { get; set; }

    // Set by EmailCaps from the injected clock, not by a database default, so the 24-hour
    // window and the pruning that enforces it read one clock.
    public DateTimeOffset SentAt { get; set; }
}
