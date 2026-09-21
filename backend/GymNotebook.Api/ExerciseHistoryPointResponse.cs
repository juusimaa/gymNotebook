namespace GymNotebook.Api;

// One plotted workout in an exercise's history. Weight and reps identify the
// working set that produced Value, so the progress screen can explain each point
// instead of showing an opaque calculated number.
public record ExerciseHistoryPointResponse(
    int WorkoutId,
    DateOnly Date,
    DateTimeOffset StartedAt,
    decimal? Weight,
    int Reps,
    decimal Value);
