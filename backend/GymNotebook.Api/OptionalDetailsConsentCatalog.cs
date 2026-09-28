namespace GymNotebook.Api;

// The consent statement for the optional workout details (specs/001 user story 6, FR-030,
// FR-034), loaded once at startup from docs/privacy/consent/, embedded by
// GymNotebook.Api.csproj exactly like the notices. Trusted server configuration: granting
// consent compares the client's version string against Current and never stores client
// text it hasn't matched here.
//
// Deliberately simpler than PrivacyNoticeCatalog. A statement file has the notice's shape
// (version, dates, summary, plain-text sections), so it reuses PrivacyNoticeVersion and
// the same validation. But there is one current version and no announced successor, and
// so no clock: changing what the statement covers needs its own specification change,
// including what happens to existing consents (FR-034), not a timed switch-over.
public sealed class OptionalDetailsConsentCatalog
{
    private const string ResourcePrefix = "OptionalDetailsConsent/";
    private const string Kind = "Consent statement";
    private const string Folder = "docs/privacy/consent/";

    public OptionalDetailsConsentCatalog(PrivacyNoticeVersion current)
    {
        PrivacyNoticeCatalog.ValidateDocument(current, Kind);
        Current = current;
    }

    // The statement a grant must name.
    public PrivacyNoticeVersion Current { get; }

    // Reads index.json and every version it lists, validating each, as the notice catalog
    // does: a broken superseded file fails at boot and in CI, not years later.
    public static OptionalDetailsConsentCatalog LoadEmbedded()
    {
        var index = PrivacyNoticeCatalog.ReadEmbedded<PrivacyNoticeIndex>(ResourcePrefix, "index.json", Kind, Folder);
        if (index.Versions is not { Count: > 0 } || string.IsNullOrWhiteSpace(index.Current))
        {
            throw new InvalidOperationException("Consent statement index.json must name a current version and list at least one version.");
        }
        if (index.AnnouncedSuccessor is not null)
        {
            throw new InvalidOperationException("Consent statement index.json must not announce a successor: a new statement needs its own specification change (FR-034).");
        }

        PrivacyNoticeVersion? current = null;
        var seen = new HashSet<string>();
        foreach (var entry in index.Versions)
        {
            var statement = PrivacyNoticeCatalog.ReadEmbedded<PrivacyNoticeVersion>(ResourcePrefix, entry.File, Kind, Folder);
            if (statement.Version != entry.Version)
            {
                throw new InvalidOperationException($"Consent statement {entry.File} declares version '{statement.Version}', but index.json lists it as '{entry.Version}'.");
            }
            PrivacyNoticeCatalog.ValidateDocument(statement, Kind);
            if (!seen.Add(statement.Version))
            {
                throw new InvalidOperationException($"Consent statement version '{statement.Version}' is listed more than once in index.json.");
            }
            if (statement.Version == index.Current)
            {
                current = statement;
            }
        }

        return new OptionalDetailsConsentCatalog(current
            ?? throw new InvalidOperationException($"Consent statement index.json points at version '{index.Current}', which it does not list."));
    }
}
