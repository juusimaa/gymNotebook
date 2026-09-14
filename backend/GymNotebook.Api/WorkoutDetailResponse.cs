namespace GymNotebook.Api;

public record WorkoutDetailResponse(
    int Id,
    DateOnly Date,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Title,
    decimal? BodyweightKg,
    string? Location,
    string? Notes,
    List<WorkoutExerciseResponse> Exercises
);
