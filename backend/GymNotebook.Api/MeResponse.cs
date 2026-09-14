namespace GymNotebook.Api;

// Body of GET /auth/me. The id is what the token's "sub" claim carries; the username is
// there because the cover page greets its owner by name and nothing else returns it —
// the JWT deliberately carries no name claim (see the /me handler in Program.cs).
public record MeResponse(int UserId, string Username);
