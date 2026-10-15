namespace GymNotebook.Api;

// POST /workouts/{id}/start. Date and StartedAt are nullable only so a missing one reads
// as 400 invalid_request rather than a default value: the client sends its local calendar
// day and the moment of the tap, as when it creates a page (specs/006 contracts/api.md).
public record StartWorkoutRequest(
    DateOnly? Date,
    DateTimeOffset? StartedAt,
    int? ExpectedRevision
);
