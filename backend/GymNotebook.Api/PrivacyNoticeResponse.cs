namespace GymNotebook.Api;

// GET /privacy/notice (specs/001 contracts/api.md → Notice document): the notice in
// effect now, plus the next version while it is only announced. Public and identical for
// every caller, so it carries no account data.
public record PrivacyNoticeResponse(
    string Version,
    DateTimeOffset EffectiveAt,
    DateTimeOffset PublishedAt,
    string MaterialChangeSummary,
    IReadOnlyList<PrivacyNoticeSection> Sections,
    AnnouncedNoticeResponse? AnnouncedSuccessor);
