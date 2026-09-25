namespace GymNotebook.Api;

// A notice version that has been announced but has not taken effect yet (FR-003:
// material changes are communicated before they apply). Shown alongside the current
// notice; it does not affect whether acknowledgement is required until effectiveAt.
public record AnnouncedNoticeResponse(
    string Version,
    DateTimeOffset EffectiveAt,
    string MaterialChangeSummary,
    IReadOnlyList<PrivacyNoticeSection> Sections);
