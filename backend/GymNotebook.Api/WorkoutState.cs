namespace GymNotebook.Api;

// The three moments of one page (specs/006 research R1). Derived from the timestamps
// rather than stored, as "in progress" already was: a stored state column could
// disagree with the timestamps that actually say when the page started and ended.
public enum WorkoutState
{
    Prepared,
    InProgress,
    Finished,
}

public static class WorkoutStates
{
    // Prepared when nobody has started the page, in progress until it has an end time,
    // finished after that. startedAt is nullable ahead of prepared pages (PR 3); until
    // then every stored page has one, so no page is prepared yet.
    public static WorkoutState Of(DateTimeOffset? startedAt, DateTimeOffset? endedAt) =>
        startedAt is null ? WorkoutState.Prepared
        : endedAt is null ? WorkoutState.InProgress
        : WorkoutState.Finished;
}
