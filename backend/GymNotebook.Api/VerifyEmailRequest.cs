namespace GymNotebook.Api;

// Body of POST /auth/verify-email. The token is what the confirmation link carried after
// "#token=" — the page reads it from the fragment and posts it here (specs/002 FR-010).
public record VerifyEmailRequest(string? Token);
