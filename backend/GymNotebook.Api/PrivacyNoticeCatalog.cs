using System.Text.Json;

namespace GymNotebook.Api;

// One section of a notice: a heading and plain-text paragraphs. Deliberately no HTML or
// markup field — the UI renders these as text (contracts/api.md → Notice document), so
// nothing in a notice file can inject markup into the page.
public record PrivacyNoticeSection(string Id, string Heading, IReadOnlyList<string> Paragraphs);

// One published notice version, as stored in docs/privacy/notices/<version>.json
// (data-model.md → PrivacyNoticeVersion). The files also carry operator-only metadata
// (owner, review date, review evidence); System.Text.Json skips properties a record
// doesn't declare, so those stay in the repository and never reach the API response.
public record PrivacyNoticeVersion(
    string Version,
    DateTimeOffset EffectiveAt,
    DateTimeOffset PublishedAt,
    string MaterialChangeSummary,
    IReadOnlyList<PrivacyNoticeSection> Sections);

// docs/privacy/notices/index.json: which version is current, which (if any) is announced
// for the future, and every version ever published — superseded ones stay listed for
// accountability (FR-003).
internal record PrivacyNoticeIndex(string Current, string? AnnouncedSuccessor, IReadOnlyList<PrivacyNoticeIndexEntry> Versions);

internal record PrivacyNoticeIndexEntry(string Version, string File);

// The notices the API can serve, loaded once at startup from the resources embedded by
// GymNotebook.Api.csproj (specs/001 T031). Trusted server configuration: the
// acknowledgement endpoint compares the client's version string against Current(), never
// stores client text it hasn't matched here (data-model.md).
//
// Which version is "current" depends on the time. Before the announced successor's
// effectiveAt, the index's current version is current and the successor is shown as an
// announced change; from effectiveAt on, the successor is current and the gate asks
// everyone again (FR-003, research R2). No deploy is needed at the switch-over — the
// clock does it — so the time is a parameter (the injected TimeProvider in the
// endpoints, a settable clock in tests).
public sealed class PrivacyNoticeCatalog
{
    // Matches the database column (AppDbContext: AcknowledgedPrivacyNoticeVersion, max 64),
    // so any version this catalog accepts can be stored.
    public const int MaxVersionLength = 64;

    private const string ResourcePrefix = "PrivacyNotices/";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly PrivacyNoticeVersion _current;
    private readonly PrivacyNoticeVersion? _announcedSuccessor;

    public PrivacyNoticeCatalog(PrivacyNoticeVersion current, PrivacyNoticeVersion? announcedSuccessor)
    {
        Validate(current);
        if (announcedSuccessor is not null)
        {
            Validate(announcedSuccessor);
            if (announcedSuccessor.Version == current.Version)
            {
                throw new InvalidOperationException("The announced successor must have a different version from the current notice.");
            }
            if (announcedSuccessor.EffectiveAt <= current.EffectiveAt)
            {
                throw new InvalidOperationException("The announced successor must take effect after the current notice.");
            }
        }

        _current = current;
        _announcedSuccessor = announcedSuccessor;
    }

    // The version the notebook gate asks for at `now`.
    public PrivacyNoticeVersion Current(DateTimeOffset now) =>
        _announcedSuccessor is not null && now >= _announcedSuccessor.EffectiveAt ? _announcedSuccessor : _current;

    // The successor, while it is still only announced; null once it has taken effect.
    public PrivacyNoticeVersion? AnnouncedSuccessor(DateTimeOffset now) =>
        _announcedSuccessor is not null && now < _announcedSuccessor.EffectiveAt ? _announcedSuccessor : null;

    // Reads index.json and every version it lists from the embedded resources. Every
    // listed version is parsed and validated, not just the two in use, so a broken
    // superseded file fails the build's tests rather than lying unnoticed. Any problem
    // throws: Program.cs calls this at startup, so a mis-packaged image fails at boot.
    public static PrivacyNoticeCatalog LoadEmbedded()
    {
        var index = Read<PrivacyNoticeIndex>("index.json");
        if (index.Versions is not { Count: > 0 } || string.IsNullOrWhiteSpace(index.Current))
        {
            throw new InvalidOperationException("Privacy notice index.json must name a current version and list at least one version.");
        }

        var versions = new Dictionary<string, PrivacyNoticeVersion>();
        foreach (var entry in index.Versions)
        {
            var notice = Read<PrivacyNoticeVersion>(entry.File);
            if (notice.Version != entry.Version)
            {
                throw new InvalidOperationException($"Privacy notice {entry.File} declares version '{notice.Version}', but index.json lists it as '{entry.Version}'.");
            }
            Validate(notice);
            if (!versions.TryAdd(notice.Version, notice))
            {
                throw new InvalidOperationException($"Privacy notice version '{notice.Version}' is listed more than once in index.json.");
            }
        }

        return new PrivacyNoticeCatalog(
            Find(versions, index.Current),
            index.AnnouncedSuccessor is null ? null : Find(versions, index.AnnouncedSuccessor));
    }

    private static PrivacyNoticeVersion Find(Dictionary<string, PrivacyNoticeVersion> versions, string version) =>
        versions.TryGetValue(version, out var notice)
            ? notice
            : throw new InvalidOperationException($"Privacy notice index.json points at version '{version}', which it does not list.");

    private static T Read<T>(string file)
    {
        using var stream = typeof(PrivacyNoticeCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + file)
            ?? throw new InvalidOperationException($"Privacy notice resource '{file}' is not embedded. Is docs/privacy/notices/ in the build?");
        return JsonSerializer.Deserialize<T>(stream, _json)
            ?? throw new InvalidOperationException($"Privacy notice resource '{file}' is empty.");
    }

    // A record deserialized from JSON gets null for a missing property even where the C#
    // type says non-null, so the "required" fields are checked here. No check for
    // placeholder wording: that is a human review gate (tasks.md T043), not code.
    private static void Validate(PrivacyNoticeVersion notice)
    {
        if (string.IsNullOrWhiteSpace(notice.Version) || notice.Version.Length > MaxVersionLength)
        {
            throw new InvalidOperationException($"A privacy notice version must be 1–{MaxVersionLength} characters.");
        }
        if (string.IsNullOrWhiteSpace(notice.MaterialChangeSummary) || notice.Sections is not { Count: > 0 })
        {
            throw new InvalidOperationException($"Privacy notice '{notice.Version}' needs a materialChangeSummary and at least one section.");
        }
        foreach (var section in notice.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id) || string.IsNullOrWhiteSpace(section.Heading)
                || section.Paragraphs is not { Count: > 0 } || section.Paragraphs.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException($"Privacy notice '{notice.Version}' has a section without an id, heading or paragraphs.");
            }
        }
        if (notice.Sections.Select(s => s.Id).Distinct().Count() != notice.Sections.Count)
        {
            throw new InvalidOperationException($"Privacy notice '{notice.Version}' has duplicate section ids.");
        }
    }
}
