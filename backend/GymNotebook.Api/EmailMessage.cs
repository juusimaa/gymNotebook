namespace GymNotebook.Api;

// One outgoing email, as EmailTemplates builds it and an IEmailSender delivers it. Kind
// is a fixed label ("confirmation", "password_reset", ...) and the only part of a message
// that is safe to log: To is a person's address, and Text and Html can carry a live
// sign-in-grade link (specs/002 FR-010).
public sealed record EmailMessage(string Kind, string To, string Subject, string Text, string Html)
{
    // A record's compiler-generated ToString prints every property, so logging a message
    // by accident ("{Message}", or an exception that embeds it) would write the address
    // and the link to the logs. Overriding it makes the safe output the default one.
    public override string ToString() => $"EmailMessage {{ Kind = {Kind} }}";
}
