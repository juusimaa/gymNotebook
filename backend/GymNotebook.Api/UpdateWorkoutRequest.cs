namespace GymNotebook.Api;

public record UpdateWorkoutRequest(
    DateOnly? Date,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Title,
    decimal? BodyweightKg,
    string? Location,
    string? Notes);
