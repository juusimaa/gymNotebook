namespace GymNotebook.Api;

// Body of POST /auth/register. InviteCode is nullable because it's optional from the
// client's side; whether it's *required* is decided by the server's INVITE_CODE setting
// (see PLAN.md, Auth section), and the handler compares the two.
public record RegisterRequest(string Username, string Password, string? InviteCode);
