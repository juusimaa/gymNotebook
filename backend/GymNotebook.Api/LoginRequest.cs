namespace GymNotebook.Api;

// Body of POST /auth/login. Minimal APIs binds it from the request's JSON automatically
// because it's a complex type with no other binding source.
public record LoginRequest(string Username, string Password);
