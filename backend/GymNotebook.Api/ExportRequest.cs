namespace GymNotebook.Api;

// Body of POST /account/export (specs/001 contracts/api.md). Only the current password:
// the account is the caller's own, identified by the bearer token, and a stolen token alone
// must not be enough to take a copy of the notebook. The value is compared as sent, never
// trimmed or otherwise transformed.
public record ExportRequest(string CurrentPassword);
