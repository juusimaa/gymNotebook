namespace GymNotebook.Api;

// Body of POST /account/delete (specs/001 contracts/api.md). The current password, as for
// the export, plus an explicit confirmation: the request itself must say the caller means
// it, so a client bug that posts the password alone can never delete an account. A missing
// confirmDeletion binds as false and is refused the same way as an explicit false.
public record DeleteAccountRequest(string CurrentPassword, bool ConfirmDeletion);
