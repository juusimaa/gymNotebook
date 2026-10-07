namespace GymNotebook.Api;

// Cloudflare Turnstile settings, read and checked once at startup (specs/002 plan D9,
// FR-019). TURNSTILE_SECRET_KEY unset or blank switches the check off, which is what local
// development and the test suite run; the frontend shows the widget only when its own
// TURNSTILE_SITE_KEY is set, so the two are switched on together.
//
// A class rather than a record for the same reason as EmailOptions: a record's generated
// ToString would print SecretKey.
public sealed class TurnstileOptions
{
    // Null when the check is off.
    public string? SecretKey { get; init; }

    // The frontend hostnames a token may have been solved on. Cloudflare says a token is
    // good, not that it came from this site: another page using the same site key would
    // produce valid tokens too. Production lists only its own domain, never localhost.
    public required IReadOnlySet<string> Hostnames { get; init; }

    public bool Enabled => SecretKey is not null;

    // Compose turns a variable left blank in .env into "" rather than absent, so blank is
    // treated the same as missing, as in EmailOptions.
    public static TurnstileOptions Load(IConfiguration configuration)
    {
        var secretKey = string.IsNullOrWhiteSpace(configuration["TURNSTILE_SECRET_KEY"])
            ? null
            : configuration["TURNSTILE_SECRET_KEY"]!.Trim();

        // Hostnames are compared case-insensitively: DNS names are, and Cloudflare reports
        // the one the browser used.
        var hostnames = (configuration["TURNSTILE_HOSTNAMES"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A secret with no hostnames would refuse every signup and reset (no token's
        // hostname can match an empty list). The reference project found that out at the
        // first signup; failing the boot finds it at the deploy. The message names the
        // settings, never the secret's value.
        if (secretKey is not null && hostnames.Count == 0)
        {
            throw new InvalidOperationException(
                "TURNSTILE_SECRET_KEY is set, so TURNSTILE_HOSTNAMES must list at least one hostname.");
        }

        return new TurnstileOptions { SecretKey = secretKey, Hostnames = hostnames };
    }
}
