namespace GymNotebook.Api;

// Where a set stands in its session (specs/006 research R2). Stored as the lowercase
// strings "planned", "done" and "skipped" (see AppDbContext) so the column, the API and
// the export all read the same word. Only Done counts as training (research R4).
public enum SetStatus
{
    // Written down ahead of time and not yet lifted.
    Planned,
    // Lifted. The only status any history, hint or count reads.
    Done,
    // Was still planned when the page was finished. Kept, so the plan stays visible.
    Skipped,
}
