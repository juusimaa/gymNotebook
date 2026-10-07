namespace GymNotebook.Api;

// Body of POST /auth/password-reset/confirm. The token is what the reset link carried
// after "#token=" — the page reads it from the fragment and posts it here with the new
// password (specs/002 FR-010).
public record ConfirmPasswordResetRequest(string? Token, string? NewPassword);
