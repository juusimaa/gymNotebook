namespace GymNotebook.Api;

// POST /workouts/prepared: only the heading, since a prepared page has no date or start
// until POST /workouts/{id}/start (specs/006). Blocks and sets follow with
// PUT /workouts/{id}/exercises, as on any page.
public record CreatePreparedWorkoutRequest(
    string? Title,
    decimal? BodyweightKg,
    string? Location,
    string? Notes
);
