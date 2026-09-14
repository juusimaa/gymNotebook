namespace GymNotebook.Api;

// Body of GET /auth/me. Only the id: it's what the token's "sub" claim carries, and the
// endpoint exists to prove a token still works, not to be a profile.
public record MeResponse(int UserId);
