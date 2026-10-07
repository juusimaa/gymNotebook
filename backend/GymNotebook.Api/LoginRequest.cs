namespace GymNotebook.Api;

// Body of POST /auth/login. Minimal APIs binds it from the request's JSON automatically
// because it's a complex type with no other binding source. The email is normalized
// (AccountInput.NormalizeEmail) before the lookup, so case and surrounding spaces don't
// matter (specs/002 Story 2, scenario 1).
public record LoginRequest(string? Email, string? Password);
