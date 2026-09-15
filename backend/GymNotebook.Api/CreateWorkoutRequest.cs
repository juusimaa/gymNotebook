namespace GymNotebook.Api;

public record CreateWorkoutRequest(
    DateOnly Date,
    DateTimeOffset StartedAt,
    string? Title,
    decimal? BodyweightKg,
    string? Location,
    string? Notes
);
