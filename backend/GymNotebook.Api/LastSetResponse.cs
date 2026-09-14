namespace GymNotebook.Api;

// The "last time" hint next to an autocomplete suggestion: the load of the most recent
// set logged for an exercise, so the new page can show "60 kg × 12" before a weight is
// typed. Weight is null for an unloaded bodyweight set, same as SetEntryResponse.
// The whole record is null on ExerciseResponse when the exercise has never had a set —
// one null rather than two loose nullable fields.
public record LastSetResponse(decimal? Weight, int Reps);
