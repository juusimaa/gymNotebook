namespace GymNotebook.Api;

// Body of POST /auth/verification: send the confirmation link again. The password is
// required so that this route can't be used to make the app email any address on demand
// — only someone who knows the account's password triggers a send (specs/002 FR-012).
public record ResendVerificationRequest(string? Email, string? Password);
