namespace GymNotebook.Api;

// PUT /account/privacy/optional-details-consent (specs/001 user story 6). The statement
// version the consent screen displayed; only the current one is accepted. Nullable because
// a body without it deserializes to null, whatever the type says — the handler answers 400.
public record GrantOptionalDetailsConsentRequest(string? StatementVersion);
