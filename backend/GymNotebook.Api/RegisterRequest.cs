namespace GymNotebook.Api;

// Body of POST /auth/register (specs/002 contracts/api.md). Nullable strings because a
// client can leave any of them out; the handler checks them all (AccountInput) and
// answers 400 rather than letting a missing field surface as a binding error.
//
// InviteCode is nullable because it's optional from the client's side; whether it's
// *required* is decided by the server's INVITE_CODE setting (see PLAN.md, Auth section),
// and the handler compares the two. It goes away in specs/002 PR 5.
public record RegisterRequest(string? Email, string? Password, string? DisplayName, string? InviteCode);
