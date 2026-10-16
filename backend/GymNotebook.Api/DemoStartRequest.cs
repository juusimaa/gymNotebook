namespace GymNotebook.Api;

// Body of POST /auth/demo (specs/007 contracts/api.md). The offset is the browser's
// distance from UTC in minutes (-new Date().getTimezoneOffset()), the only client input
// the seed uses: "today" is the visitor's day, but every timestamp comes from the
// server's clock, so a wrong device clock can't date pages in the future. Nullable so a
// missing offset is a 400 invalid_request, not a silent 0.
public record DemoStartRequest(string? TurnstileToken, int? UtcOffsetMinutes);
