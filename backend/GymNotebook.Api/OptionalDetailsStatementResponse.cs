namespace GymNotebook.Api;

// GET /privacy/optional-details-statement: the notice document's shape without an
// announced successor (contracts/api.md). Operator metadata in the file isn't included.
public record OptionalDetailsStatementResponse(
    string Version,
    DateTimeOffset EffectiveAt,
    DateTimeOffset PublishedAt,
    string MaterialChangeSummary,
    IReadOnlyList<PrivacyNoticeSection> Sections);
