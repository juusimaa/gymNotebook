namespace GymNotebook.Api;

// Body of GET /auth/me. The id is what the token's "sub" claim carries; the display name
// and email are there because the cover page shows both — the name on the notebook, the
// address beside the account links so the user can tell which account this browser is in
// (specs/002 contracts/ui.md → Cover). The JWT deliberately carries neither (see the /me
// handler in Program.cs).
public record MeResponse(int UserId, string DisplayName, string Email);
