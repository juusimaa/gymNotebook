namespace GymNotebook.Api;

// Email settings, read and checked once at startup (specs/002 plan D7 and Configuration).
// Every rule here fails the boot rather than the first email: a production deploy that
// silently sends nothing would leave people unable to confirm an account or reset a
// password, and nobody would notice until they complained.
//
// A class rather than a record on purpose: a record's generated ToString prints every
// property, and ResendApiKey is a secret.
public sealed class EmailOptions
{
    public const string ConsoleBackend = "console";
    public const string MemoryBackend = "memory";
    public const string ResendBackend = "resend";

    // Under Resend's free tier of 100 a day, so the provider never starts refusing mail —
    // which would take password reset down with everything else (spec FR-015).
    public const int DefaultDailyCap = 90;

    public required string Backend { get; init; }

    // The From header, e.g. "Gym Notebook <no-reply@mail.gymnotebook.fit>". Only Resend
    // checks it against a verified domain; the other backends never send.
    public required string From { get; init; }

    // Where links in emails point: the frontend, not this API. No trailing slash, so
    // EmailTemplates can append "/verify-email#token=..." without doubling it.
    public required string AppUrl { get; init; }

    // Null for every backend but resend.
    public string? ResendApiKey { get; init; }

    // EMAIL_DAILY_CAP: emails the whole app may send in any rolling 24 hours (EmailCaps).
    public required int DailyCap { get; init; }

    // Configuration keys follow the plan's table: Email__Backend reaches IConfiguration as
    // "Email:Backend"; the others are flat names, as in the reference project and the
    // deploy secrets. Compose turns a variable left blank in .env into "" rather than
    // absent, so every optional value treats blank the same as missing.
    public static EmailOptions Load(IConfiguration configuration, bool isProduction)
    {
        var backend = (Blank(configuration["Email:Backend"]) ?? ConsoleBackend).ToLowerInvariant();

        // Console logs live links; memory piles messages up in RAM and sends nothing. Both
        // are fine on a developer's machine — an SDK run and the test host are Development,
        // and Compose runs as "Local" (docker-compose.yml) — and neither is ever right in
        // Production, which is what Azure runs: ASP.NET Core's default when
        // ASPNETCORE_ENVIRONMENT is unset. Keyed on Production rather than "not
        // Development" (owner decision, 2026-10-06) so Compose can keep Scalar and the
        // developer exception page off while still printing links to its log.
        if (backend is ConsoleBackend or MemoryBackend)
        {
            if (isProduction)
            {
                throw new InvalidOperationException(
                    $"Email:Backend '{backend}' is not allowed in Production. Use '{ResendBackend}'.");
            }
        }
        else if (backend != ResendBackend)
        {
            throw new InvalidOperationException(
                $"Email:Backend must be '{ConsoleBackend}', '{MemoryBackend}' or '{ResendBackend}'.");
        }

        var isResend = backend == ResendBackend;
        var apiKey = Blank(configuration["RESEND_API_KEY"]);
        var from = Blank(configuration["EMAIL_FROM"]);
        var appUrl = Blank(configuration["APP_URL"]);

        // The messages name the setting, never its value: the key is a secret.
        if (isResend && (apiKey is null || from is null || appUrl is null))
        {
            throw new InvalidOperationException(
                "Email:Backend 'resend' needs RESEND_API_KEY, EMAIL_FROM and APP_URL to be set.");
        }

        // Development defaults: the Vite dev server, and a From nobody will ever receive.
        appUrl ??= "http://localhost:5173";
        if (!Uri.TryCreate(appUrl, UriKind.Absolute, out var parsedAppUrl)
            || (parsedAppUrl.Scheme != Uri.UriSchemeHttps && parsedAppUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("APP_URL must be an absolute http(s) URL.");
        }

        // GetValue throws its own (value-quoting) error for a non-number, which is fine
        // for a cap; a zero or negative cap would silently disable every email.
        var dailyCap = Blank(configuration["EMAIL_DAILY_CAP"]) is null
            ? DefaultDailyCap
            : configuration.GetValue<int>("EMAIL_DAILY_CAP");
        if (dailyCap < 1)
        {
            throw new InvalidOperationException("EMAIL_DAILY_CAP must be at least 1.");
        }

        return new EmailOptions
        {
            Backend = backend,
            From = from ?? "Gym Notebook <no-reply@localhost>",
            AppUrl = appUrl.TrimEnd('/'),
            ResendApiKey = isResend ? apiKey : null,
            DailyCap = dailyCap,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
