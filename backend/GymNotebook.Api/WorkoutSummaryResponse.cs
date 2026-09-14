namespace GymNotebook.Api;

// One row of GET /workouts. Carries everything the sessions list draws — the date and
// start time that order it, the title that makes it scannable, and the exercise names,
// counts and end time for the row's summary and meta lines — so the list never has to
// fetch each page in full to render itself. Heading fields the list doesn't show
// (bodyweight, location, notes) stay on WorkoutDetailResponse.
public record WorkoutSummaryResponse(
    int Id,
    DateOnly Date,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Title,
    int ExerciseCount,
    int SetCount,
    List<string> ExerciseNames
);
