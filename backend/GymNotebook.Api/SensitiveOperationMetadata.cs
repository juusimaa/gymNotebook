namespace GymNotebook.Api;

// Endpoint metadata marking a password-verified account operation (export now, deletion in
// US4) for the per-account rate limit in Program.cs (specs/001 research R10, P12). A marker
// rather than a second named policy because the rate-limiting middleware honours only one
// named policy per endpoint, and these endpoints keep the existing per-IP "auth" policy too:
// the per-account bucket is the global limiter, which counts only endpoints carrying this.
public sealed class SensitiveOperationMetadata;
