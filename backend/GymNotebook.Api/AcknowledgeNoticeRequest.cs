namespace GymNotebook.Api;

// PUT /account/privacy/acknowledgement. The version the gate displayed; accepted only if
// it is still the current one. Nullable so a missing value reaches the handler's own 400
// invalid_request rather than a framework binding error.
public record AcknowledgeNoticeRequest(string? NoticeVersion);
