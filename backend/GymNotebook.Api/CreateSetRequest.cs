namespace GymNotebook.Api;

public record CreateSetRequest(string ExerciseName, decimal? Weight, int Reps, bool IsWarmup);
