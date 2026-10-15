namespace GymNotebook.Api;

// Status is optional, as on PutSetInput: omitted means the page state's default.
public record CreateSetRequest(string ExerciseName, decimal? Weight, int Reps, bool IsWarmup, int? ExpectedRevision = null, string? Status = null);
