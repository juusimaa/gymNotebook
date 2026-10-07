namespace GymNotebook.Api;

// Body of POST /auth/register (specs/002 contracts/api.md). Nullable strings because a
// client can leave any of them out; the handler checks them all (AccountInput) and
// answers 400 rather than letting a missing field surface as a binding error.
//
// TurnstileToken is what the Cloudflare widget gave the browser. Optional from the
// client's side: whether it's *required* is decided by the server's TURNSTILE_SECRET_KEY
// (see Turnstile.cs), and without one the field is ignored.
public record RegisterRequest(string? Email, string? Password, string? DisplayName, string? TurnstileToken = null);
