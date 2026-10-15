namespace GymNotebook.Api;

// The one place that says which set statuses a page may hold (specs/006 research R3).
// The rule spans two tables (the page's timestamps and its sets' status), so the
// database can't check it; every set write route asks this class instead.
//
//   page state   | allowed          | when the client omits status
//   -------------|------------------|-----------------------------
//   prepared     | planned          | planned
//   in progress  | planned, done    | done
//   finished     | done, skipped    | done
//
// A done set on a page nobody started, or a planned set on a finished page, would
// mean nothing, so such a write is refused with 400 invalid_set_status, never coerced.
// The transitions between states (finish: planned → skipped, reopen: skipped → planned)
// live in PATCH /workouts/{id}, next to the timestamp change that causes them.
public static class SetStatusRules
{
    public const string InvalidSetStatus = "invalid_set_status";

    public static bool IsAllowed(WorkoutState state, SetStatus status) => state switch
    {
        WorkoutState.Prepared => status == SetStatus.Planned,
        WorkoutState.InProgress => status is SetStatus.Planned or SetStatus.Done,
        WorkoutState.Finished => status is SetStatus.Done or SetStatus.Skipped,
        _ => false,
    };

    // Omitted status keeps every existing client working: today's editor never sends
    // one, and on a started page it means "I lifted this", as it always has.
    public static SetStatus DefaultFor(WorkoutState state) =>
        state == WorkoutState.Prepared ? SetStatus.Planned : SetStatus.Done;

    // The wire and column form. Lowercase words rather than enum names or numbers, so
    // psql, the API and the export file all read "skipped" (research R2).
    public static string ToText(SetStatus status) => status switch
    {
        SetStatus.Planned => "planned",
        SetStatus.Done => "done",
        SetStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static SetStatus FromText(string text) =>
        TryParse(text, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown set status.");

    // Exact lowercase only: the API documents three words, and accepting "Done" or "1"
    // would make a second spelling clients could start depending on.
    public static bool TryParse(string? text, out SetStatus status)
    {
        switch (text)
        {
            case "planned": status = SetStatus.Planned; return true;
            case "done": status = SetStatus.Done; return true;
            case "skipped": status = SetStatus.Skipped; return true;
            default: status = default; return false;
        }
    }

    // What a write route stores for a requested status on a page in this state: the
    // default when omitted, the parsed value when allowed, or null when the request
    // must be refused (unknown word, or a status the page state doesn't allow).
    public static SetStatus? Resolve(WorkoutState state, string? requested)
    {
        if (requested is null)
        {
            return DefaultFor(state);
        }

        return TryParse(requested, out var status) && IsAllowed(state, status) ? status : null;
    }
}
