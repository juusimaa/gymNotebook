namespace GymNotebook.Api;

public record WorkoutSummaryResponse(int Id, DateOnly Date, DateTimeOffset StartedAt, string? Title);
