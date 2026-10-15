namespace GymNotebook.Api;

// Status is optional: omitted means done (SetStatusRules.DefaultStatus),
// so a client that predates statuses keeps saving lifted sets.
public record PutSetInput(decimal? Weight, int Reps, bool IsWarmup, string? Status = null);
