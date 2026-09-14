namespace GymNotebook.Api;

// Body of every endpoint that mints a JWT (/auth/register, /auth/login,
// /auth/change-password). A positional record: immutable, compared by value, and
// System.Text.Json serializes it with no extra ceremony — all a response DTO needs.
public record AuthResponse(string Token);
