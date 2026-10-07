namespace GymNotebook.Api;

// Body of a successful POST /auth/verify-email: the address that is now confirmed, so the
// sign-in screen can be opened with it already filled in (specs/002 contracts/ui.md).
public record VerifyEmailResponse(string Email);
