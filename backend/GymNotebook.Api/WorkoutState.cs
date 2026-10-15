namespace GymNotebook.Api;

// The two moments of a page (specs/006 research R1, revised by R15). Derived from the
// end time rather than stored, as "in progress" always was: a stored state column could
// disagree with the timestamp that actually says when the page ended.
public enum WorkoutState
{
    InProgress,
    Finished,
}

public static class WorkoutStates
{
    // In progress until the page has an end time, finished after that.
    public static WorkoutState Of(DateTimeOffset? endedAt) =>
        endedAt is null ? WorkoutState.InProgress : WorkoutState.Finished;
}
