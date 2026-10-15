namespace GymNotebook.Api;

// Status is optional: omitted means the page state's default (SetStatusRules.DefaultFor),
// so a client that predates statuses keeps saving lifted sets.
public record PutSetInput(decimal? Weight, int Reps, bool IsWarmup, string? Status = null);
