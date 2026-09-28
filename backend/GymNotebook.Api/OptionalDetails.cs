using System.Linq.Expressions;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// What a workout create or update may do with the optional details it carries.
public enum OptionalDetailsDecision
{
    // Store them as sent: the feature is off, the request carries none, or the account
    // holds consent.
    Store,

    // No consent, but every detail the request sets is null or empty: accepted (clearing
    // is always allowed), with empty strings stored as null. That keeps "the account
    // holds a detail" meaning "a detail is not null" everywhere — in the transition
    // question, withdrawal's count and the operator's clearing SQL.
    StoreAsNull,

    // No consent and at least one non-empty detail: reject the whole request with 403
    // optional_details_consent_required, storing nothing from it (FR-032).
    Reject,
}

// The optional workout details — Workout Title, Location, Notes and BodyweightKg — may be
// stored only for an account holding consent (specs/001 user story 6, FR-029–FR-033).
// Exercise names are outside the consent. Enforcement is server-side, in the workout
// handlers (Program.cs); hiding the inputs in the UI is not enough.
//
// Coordination with withdrawal. A save and a withdrawal both run under *shared* lifecycle
// access, so the advisory lock doesn't order them — the account's users row does:
//
//   - A save that sets a detail reads the consent with FOR SHARE, and holds that row lock
//     until its transaction commits.
//   - A withdrawal first clears the consent pair (an UPDATE, which waits for every FOR
//     SHARE holder), then clears the workouts in a later statement.
//
// So a save that read the consent before the withdrawal commits its workout first, and the
// withdrawal's later statement — READ COMMITTED, a fresh snapshot — clears it. A save that
// arrives during the withdrawal waits for it, then reads the committed null and is
// rejected. Never a detail stored after the withdrawal (OptionalDetailsCoordinationTests).
public static class OptionalDetails
{
    public const string ConsentRequiredCode = "optional_details_consent_required";

    // "This workout holds an optional detail." For SQL (the transition check, withdrawal),
    // so it's an expression EF translates rather than a C# method.
    public static readonly Expression<Func<Workout, bool>> HoldsDetail =
        w => w.Title != null || w.Location != null || w.Notes != null || w.BodyweightKg != null;

    // `enforced` is whether PRIVACY_LIFECYCLE_ENABLED is on. The detail arguments are the
    // values the request *sets*: for a PATCH, null for a field it omits.
    public static async Task<OptionalDetailsDecision> DecideAsync(
        AppDbContext db, int userId, bool enforced,
        string? title, decimal? bodyweightKg, string? location, string? notes, CancellationToken ct)
    {
        // Nothing to decide: no row lock taken for the common "change the date" update.
        if (!enforced || (title is null && bodyweightKg is null && location is null && notes is null))
        {
            return OptionalDetailsDecision.Store;
        }

        if (await HasConsentAsync(db, userId, ct))
        {
            return OptionalDetailsDecision.Store;
        }

        var anyNonEmpty = !string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(location)
            || !string.IsNullOrEmpty(notes) || bodyweightKg is not null;
        return anyNonEmpty ? OptionalDetailsDecision.Reject : OptionalDetailsDecision.StoreAsNull;
    }

    public static IResult ConsentRequired() =>
        Results.Json(new ErrorResponse(ConsentRequiredCode), statusCode: StatusCodes.Status403Forbidden);

    public static string? EmptyAsNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // Raw SQL because EF Core has no LINQ operator for FOR SHARE. It runs in the lifecycle
    // filter's transaction (EF enlists raw queries in the current one), so the row lock
    // lasts until the save commits or rolls back. Under READ COMMITTED a locking read that
    // waited on a withdrawal returns the row as that withdrawal committed it: null.
    // ToListAsync, not SingleAsync: composing a query over this SQL would wrap it in a
    // subquery, and there's no need to. The lifecycle filter has already confirmed, under
    // its lock, that the row exists.
    private static async Task<bool> HasConsentAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var versions = await db.Database
            .SqlQuery<string?>($"SELECT optional_details_consent_version AS \"Value\" FROM users WHERE id = {userId} FOR SHARE")
            .ToListAsync(ct);
        return versions.Single() is not null;
    }
}
