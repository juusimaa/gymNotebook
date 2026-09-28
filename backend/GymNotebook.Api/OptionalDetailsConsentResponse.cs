namespace GymNotebook.Api;

// The consent record for the optional workout details: which statement version, and when.
// The grant route's response, and `consent` in the account privacy state.
public record OptionalDetailsConsentResponse(string StatementVersion, DateTimeOffset ConsentedAt);
