namespace GymNotebook.Api;

// Body of POST /auth/password-reset: "Forgot your password?". The address, and the
// Turnstile token when the check is on (see RegisterRequest) — the answer is the same
// whether or not the address has an account (specs/002 FR-013).
public record PasswordResetRequest(string? Email, string? TurnstileToken = null);
