namespace GymNotebook.Api;

// Date and StartedAt are null on a prepared page (specs/006), set on every started one.
public record WorkoutDetailResponse(
    int Id,
    int Revision,
    DateOnly? Date,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Title,
    decimal? BodyweightKg,
    string? Location,
    string? Notes,
    List<WorkoutExerciseResponse> Exercises
);
