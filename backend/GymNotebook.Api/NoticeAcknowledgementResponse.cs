namespace GymNotebook.Api;

// The account's latest "Continue" on the notice gate: which version, and when it was
// recorded. Evidence that the notice was shown, not that it was read or consented to.
public record NoticeAcknowledgementResponse(string NoticeVersion, DateTimeOffset AcknowledgedAt);
