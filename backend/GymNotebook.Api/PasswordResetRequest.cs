namespace GymNotebook.Api;

// Body of POST /auth/password-reset: "Forgot your password?". Only the address — the
// answer is the same whether or not it has an account (specs/002 FR-013).
public record PasswordResetRequest(string? Email);
