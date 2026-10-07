using System.Text;

namespace GymNotebook.Api;

// The input rules for account fields (specs/002 data-model.md, FR-001, FR-002, FR-005), in
// one place so register, login, resend and change-password can't drift apart. Static and
// pure: no state, nothing to inject, trivially testable.
public static class AccountInput
{
    // The longest address SMTP can deliver to (RFC 5321's path limit).
    public const int EmailMaxLength = 254;

    public const int DisplayNameMaxLength = 50;

    // BCrypt only reads the first 72 bytes of a password and silently ignores the rest, so
    // two long passwords sharing a 72-byte prefix would both work. Refusing anything longer
    // keeps "the password you typed" and "the password that's checked" the same thing.
    // Bytes, not characters: "ä" is two bytes in UTF-8.
    public const int PasswordMaxBytes = 72;

    // Trimmed and lowercased before every write and every lookup, so the address a user
    // signs in with matches the one they registered whatever the phone's keyboard did
    // with capitals or a trailing space. Lowercasing the local part (before the @) is
    // technically stricter than RFC 5321 allows, but no real provider treats case there as
    // significant, and the alternative is two accounts for one inbox.
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    // Deliberately loose (data-model.md): exactly one "@" with something on both sides,
    // no whitespace, within the length limit. Real validation of an address is the
    // confirmation email arriving; a strict regex only ever rejects valid addresses.
    // Expects an already-normalized value.
    public static bool IsValidEmail(string normalized)
    {
        if (normalized.Length is 0 or > EmailMaxLength || normalized.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = normalized.IndexOf('@');
        return at > 0 && at == normalized.LastIndexOf('@') && at < normalized.Length - 1;
    }

    // Non-blank (the rule register and change-password have always had) and within
    // BCrypt's 72 bytes.
    public static bool IsValidPassword(string? password) =>
        !string.IsNullOrWhiteSpace(password) && Encoding.UTF8.GetByteCount(password) <= PasswordMaxBytes;

    // Trimmed, 1–50 characters. Returns null when invalid, so the caller gets the value to
    // store and the verdict in one call.
    public static string? NormalizeDisplayName(string? displayName)
    {
        var trimmed = (displayName ?? "").Trim();
        return trimmed.Length is 0 or > DisplayNameMaxLength ? null : trimmed;
    }
}
