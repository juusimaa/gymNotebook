namespace GymNotebook.Api;

// The body of the few error responses that need to tell the client *which* failure it
// was, beyond the status code (specs/001 contracts/api.md): "account_suspended" at login,
// "temporarily_unavailable" when a lifecycle lock wait times out. Most errors in this API
// stay bare statuses; a code is added only where the UI must react differently.
public record ErrorResponse(string Code);
