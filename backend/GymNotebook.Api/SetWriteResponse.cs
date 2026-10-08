namespace GymNotebook.Api;

// Incremental set writes return the new page revision alongside the set's fields.
// SetEntryResponse remains the shape nested in a workout detail response.
public record SetWriteResponse(int Id, int SetNumber, int Reps, decimal? Weight, bool IsWarmup, int Revision);
