namespace GymNotebook.Api;

// One row of GET /workouts/prepared: a page written before the gym and not started yet
// (specs/006). It has no date or start to show, so the row is its title, when it was
// written, and what it holds. SetCount is every set on it; on a prepared page they are
// all planned.
public record PreparedWorkoutSummaryResponse(
    int Id,
    string? Title,
    DateTimeOffset CreatedAt,
    List<string> ExerciseNames,
    int ExerciseCount,
    int SetCount
);
