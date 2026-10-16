namespace GymNotebook.Api;

// Body of POST /account/demo/reset (specs/007 contracts/api.md): the offset again, since
// the new notebook's "today" is the visitor's day at the moment of the reset.
public record DemoResetRequest(int? UtcOffsetMinutes);
