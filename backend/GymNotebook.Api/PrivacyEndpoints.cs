using System.Security.Claims;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// The privacy routes of specs/001 (contracts/api.md): the notice (user story 1), export
// (3), deletion (4) and the optional-details consent (6). Program.cs maps
// them only when PRIVACY_LIFECYCLE_ENABLED is exactly "true"; otherwise they don't exist
// and every one of them is a plain 404, which is also how the frontend detects that the
// feature is off (plan.md P25).
public static class PrivacyEndpoints
{
    public static void MapPrivacyEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: readable before registration (FR-001). Reading it records nothing —
        // acknowledgement happens only through the PUT below, from the gate's Continue.
        app.MapGet("/privacy/notice", (PrivacyNoticeCatalog catalog, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var current = catalog.Current(now);
            var successor = catalog.AnnouncedSuccessor(now);

            return Results.Ok(new PrivacyNoticeResponse(
                current.Version,
                current.EffectiveAt,
                current.PublishedAt,
                current.MaterialChangeSummary,
                current.Sections,
                successor is null
                    ? null
                    : new AnnouncedNoticeResponse(successor.Version, successor.EffectiveAt, successor.MaterialChangeSummary, successor.Sections)));
        })
           .WithName("GetPrivacyNotice")
           .WithTags("Privacy")
           .WithSummary("Returns the current privacy notice")
           .WithDescription("Public. The notice in effect now, plus an announced successor while it has not yet taken effect. Has no side effects.")
           .Produces<PrivacyNoticeResponse>(StatusCodes.Status200OK);

        // User story 6: the consent statement for the optional workout details. Public like
        // the notice, so the statement can be read before deciding, and with no side
        // effects: reading it is not consenting.
        app.MapGet("/privacy/optional-details-statement", (OptionalDetailsConsentCatalog catalog) =>
        {
            var statement = catalog.Current;
            return Results.Ok(new OptionalDetailsStatementResponse(
                statement.Version, statement.EffectiveAt, statement.PublishedAt, statement.MaterialChangeSummary, statement.Sections));
        })
           .WithName("GetOptionalDetailsStatement")
           .WithTags("Privacy")
           .WithSummary("Returns the current optional-details consent statement")
           .WithDescription("Public. The versioned statement shown before consenting to storing workout title, location, notes and bodyweight. Has no side effects.")
           .Produces<OptionalDetailsStatementResponse>(StatusCodes.Status200OK);

        // The account's own privacy state. A group of its own, "/account/privacy" rather
        // than "/account": the export and delete routes under /account take
        // their own guards and must NOT inherit the shared lifecycle filter (see the
        // allow-list in LifecycleCoverageTests).
        //
        // Filter order matters: group filters run outermost-first in the order added, so
        // NoStore wraps the lifecycle filter and its 401/503 results get the header too.
        var accountPrivacy = app.MapGroup("/account/privacy")
            .RequireAuthorization()
            .AddEndpointFilter(NoStore)
            .RequireAccountLifecycle()
            .WithTags("Privacy");

        accountPrivacy.MapGet("", async (ClaimsPrincipal caller, AppDbContext db, PrivacyNoticeCatalog catalog, OptionalDetailsConsentCatalog consentCatalog, TimeProvider clock, CancellationToken ct) =>
        {
            var (userId, _) = AccountLifecycle.ReadClaims(caller);
            var current = catalog.Current(clock.GetUtcNow());
            var acknowledgement = await ReadAcknowledgementAsync(db, userId, ct);
            var consent = await ReadConsentAsync(db, userId, ct);

            // The transition question (FR-035) is only for an account without consent that
            // still holds details from before the feature, and it names how many workouts
            // are affected. With consent there's nothing to ask, so no query at all.
            var pendingWorkouts = consent is null
                ? await db.Workouts.Where(w => w.UserId == userId).CountAsync(OptionalDetails.HoldsDetail, ct)
                : 0;

            // Equality, never ordering: version strings are identifiers, and "later" is
            // decided by the catalog's effective dates, not by comparing text (data-model.md).
            return Results.Ok(new AccountPrivacyResponse(
                current.Version,
                acknowledgement,
                acknowledgement?.NoticeVersion != current.Version,
                new OptionalDetailsStateResponse(consentCatalog.Current.Version, consent, pendingWorkouts > 0, pendingWorkouts)));
        })
           .WithName("GetAccountPrivacy")
           .WithSummary("Returns the caller's privacy notice and consent state")
           .WithDescription("The current notice version, the caller's latest acknowledgement (or null), and whether the notebook gate must show the notice before notebook access. Plus the optional-details consent: the current statement version, the caller's consent (or null), whether the transition question is pending, and how many workouts it concerns.")
           .Produces<AccountPrivacyResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status401Unauthorized);

        accountPrivacy.MapPut("/acknowledgement", async (AcknowledgeNoticeRequest request, ClaimsPrincipal caller, AppDbContext db, PrivacyNoticeCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            var version = request.NoticeVersion;
            if (string.IsNullOrWhiteSpace(version) || version.Length > PrivacyNoticeCatalog.MaxVersionLength)
            {
                return Results.BadRequest(new ErrorResponse("invalid_request"));
            }

            // Only the version in effect right now can be acknowledged. A tab that shows an
            // older notice (the successor took effect while it was open) gets 409 and
            // reloads the newer one, rather than recording a version that isn't current.
            var now = clock.GetUtcNow();
            if (version != catalog.Current(now).Version)
            {
                return Results.Conflict(new ErrorResponse("notice_version_changed"));
            }

            var (userId, _) = AccountLifecycle.ReadClaims(caller);

            // Idempotent in one statement: the WHERE skips the update when this version is
            // already recorded, so a repeated Continue keeps the original timestamp. Two
            // sessions continuing at the same moment are safe too — the second UPDATE waits
            // for the first's row lock, then re-checks the WHERE against the committed row
            // and matches nothing. Only the version and time are stored: no consent flag
            // exists to set (data-model.md).
            await db.Users
                .Where(u => u.Id == userId && u.AcknowledgedPrivacyNoticeVersion != version)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AcknowledgedPrivacyNoticeVersion, version)
                    .SetProperty(u => u.PrivacyNoticeAcknowledgedAt, now), ct);

            // Read back rather than echo `now`: the database stores microseconds, and the
            // same-version case must return the timestamp that was kept, not this request's.
            var acknowledgement = await ReadAcknowledgementAsync(db, userId, ct);
            return Results.Ok(acknowledgement);
        })
           .WithName("AcknowledgePrivacyNotice")
           .WithSummary("Records that the caller continued past the current notice")
           .WithDescription("Accepts only the current notice version; a stale version gets 409 notice_version_changed. Repeating the same version keeps the original timestamp. Records acknowledgement, never consent.")
           .Produces<NoticeAcknowledgementResponse>(StatusCodes.Status200OK)
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        // User story 6: consent for the optional workout details (FR-030). The same shape
        // as the acknowledgement: only the current statement version, idempotent in one
        // statement, and only the version and time stored.
        accountPrivacy.MapPut("/optional-details-consent", async (GrantOptionalDetailsConsentRequest request, ClaimsPrincipal caller, AppDbContext db, OptionalDetailsConsentCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            var version = request.StatementVersion;
            if (string.IsNullOrWhiteSpace(version) || version.Length > PrivacyNoticeCatalog.MaxVersionLength)
            {
                return Results.BadRequest(new ErrorResponse("invalid_request"));
            }

            // A screen showing any other wording than the current statement's can't be
            // what the user agreed to, so it reloads rather than records.
            if (version != catalog.Current.Version)
            {
                return Results.Conflict(new ErrorResponse("consent_statement_changed"));
            }

            var (userId, _) = AccountLifecycle.ReadClaims(caller);

            // The WHERE skips an account that already consented to this version, so a
            // retry keeps the original timestamp — the same pattern as the acknowledgement.
            await db.Users
                .Where(u => u.Id == userId && u.OptionalDetailsConsentVersion != version)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.OptionalDetailsConsentVersion, version)
                    .SetProperty(u => u.OptionalDetailsConsentedAt, clock.GetUtcNow()), ct);

            return Results.Ok(await ReadConsentAsync(db, userId, ct));
        })
           .WithName("GrantOptionalDetailsConsent")
           .WithSummary("Records the caller's consent to storing optional workout details")
           .WithDescription("Accepts only the current consent statement version; any other gets 409 consent_statement_changed. Repeating the same version keeps the original timestamp.")
           .Produces<OptionalDetailsConsentResponse>(StatusCodes.Status200OK)
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        // Withdrawal (FR-033), and "Don't allow" in the transition question. No password:
        // withdrawing must take no more effort than granting. Atomic because both
        // statements run in the lifecycle filter's transaction, which commits only on this
        // 2xx; a concurrent export's snapshot sees all of it or none of it.
        //
        // Statement order matters: the users row first, the workouts second. See
        // OptionalDetails.cs for why that stops a concurrent save from storing a detail
        // after the withdrawal.
        accountPrivacy.MapDelete("/optional-details-consent", async (ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
        {
            var (userId, _) = AccountLifecycle.ReadClaims(caller);

            // Unconditional, even when the pair is already null, so the withdrawal always
            // takes the row lock that orders it against in-flight saves.
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.OptionalDetailsConsentVersion, (string?)null)
                    .SetProperty(u => u.OptionalDetailsConsentedAt, (DateTimeOffset?)null), ct);

            // One set-based UPDATE for every workout of the caller. Only rows that hold a
            // detail are touched, so the count is what this request cleared — a retry
            // returns 0 — and nothing else on the workout (dates, sets) is changed.
            var cleared = await db.Workouts
                .Where(w => w.UserId == userId)
                .Where(OptionalDetails.HoldsDetail)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.Title, (string?)null)
                    .SetProperty(w => w.Location, (string?)null)
                    .SetProperty(w => w.Notes, (string?)null)
                    .SetProperty(w => w.BodyweightKg, (decimal?)null), ct);

            return Results.Ok(new OptionalDetailsWithdrawalResponse(cleared));
        })
           .WithName("WithdrawOptionalDetailsConsent")
           .WithSummary("Withdraws consent and clears every optional workout detail")
           .WithDescription("Removes the consent record and clears title, location, notes and bodyweight on every one of the caller's workouts, in one transaction. The rest of the notebook is unchanged. Safe to retry: a repeat returns clearedWorkouts 0.")
           .Produces<OptionalDetailsWithdrawalResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status401Unauthorized);

        // User story 3: the notebook export. Deliberately *not* under the lifecycle filter
        // (it's on LifecycleCoverageTests' allow-list): the filter would hold shared access
        // for the whole download, and deletion must be able to cut in between chunks.
        // NotebookExport takes its own initialization and per-chunk delivery guards instead.
        //
        // Rate limits: the existing per-IP "auth" policy, as for login, plus the per-account
        // limit that SensitiveOperationMetadata opts into (Program.cs). The body binds only
        // from JSON: any other Content-Type gets 415 and malformed JSON 400 from Minimal
        // APIs itself, before the handler runs.
        app.MapPost("/account/export", (ExportRequest request, NotebookExport export, HttpContext http) =>
            export.RunAsync(request, http))
           .RequireAuthorization()
           .AddEndpointFilter(NoStore)
           .RequireRateLimiting("auth")
           .WithMetadata(new SensitiveOperationMetadata())
           .WithName("ExportNotebook")
           .WithTags("Privacy")
           .WithSummary("Downloads a copy of the caller's notebook")
           .WithDescription("Verifies the current password, then streams the whole notebook as one JSON file (format version 1) read from a single database snapshot, with an embedded field guide. The stream is cut, never completed, if the account is deleted, the password changed or the token expires during the download.")
           .Accepts<ExportRequest>("application/json")
           .Produces(StatusCodes.Status200OK, contentType: "application/json")
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces(StatusCodes.Status415UnsupportedMediaType)
           .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
           .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // User story 4: permanent account deletion. Also on LifecycleCoverageTests'
        // allow-list rather than under the shared filter: AccountDeletion takes exclusive
        // access in its own transaction, and owns its commit so it can write the deletion
        // log lines around it. POST, not DELETE, so the body (password plus confirmation)
        // doesn't depend on DELETE-with-body support anywhere on the path. The same rate
        // limits as the export: per IP, plus the per-account bucket for sensitive
        // operations, which the two share.
        app.MapPost("/account/delete", (DeleteAccountRequest request, AccountDeletion deletion, HttpContext http) =>
            deletion.RunAsync(request, http))
           .RequireAuthorization()
           .AddEndpointFilter(NoStore)
           .RequireRateLimiting("auth")
           .WithMetadata(new SensitiveOperationMetadata())
           .WithName("DeleteAccount")
           .WithTags("Privacy")
           .WithSummary("Permanently deletes the caller's account")
           .WithDescription("Verifies the current password and requires confirmDeletion: true, then removes the account, its exercises, workouts and sets in one transaction and signs out every session. Returns only dates, never a token. 503 temporarily_unavailable means nothing was deleted; 503 deletion_outcome_unknown means the outcome could not be confirmed.")
           .Accepts<DeleteAccountRequest>("application/json")
           .Produces<DeletionResponse>(StatusCodes.Status200OK)
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces(StatusCodes.Status415UnsupportedMediaType)
           .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
           .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    // AsNoTracking: OnTokenValidated has already loaded and tracked this User, before the
    // lifecycle lock; a tracked query would hand back that possibly stale instance.
    // SingleAsync: the lifecycle filter confirmed under its lock that the row exists.
    private static async Task<NoticeAcknowledgementResponse?> ReadAcknowledgementAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AcknowledgedPrivacyNoticeVersion, u.PrivacyNoticeAcknowledgedAt })
            .SingleAsync(ct);

        // The check constraint keeps the two fields null or set together.
        return row.AcknowledgedPrivacyNoticeVersion is null || row.PrivacyNoticeAcknowledgedAt is null
            ? null
            : new NoticeAcknowledgementResponse(row.AcknowledgedPrivacyNoticeVersion, row.PrivacyNoticeAcknowledgedAt.Value);
    }

    // The consent pair, read the same way and for the same reasons as the acknowledgement.
    private static async Task<OptionalDetailsConsentResponse?> ReadConsentAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.OptionalDetailsConsentVersion, u.OptionalDetailsConsentedAt })
            .SingleAsync(ct);

        return row.OptionalDetailsConsentVersion is null || row.OptionalDetailsConsentedAt is null
            ? null
            : new OptionalDetailsConsentResponse(row.OptionalDetailsConsentVersion, row.OptionalDetailsConsentedAt.Value);
    }

    // Personal responses — and the errors on those routes — must not be stored by the
    // browser or any cache in between (contracts/api.md → Common behavior). Set before
    // the handler runs, so it applies whatever result comes back. Internal: the /auth
    // group in Program.cs uses it too (specs/002 contracts/api.md).
    internal static ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return next(context);
    }
}
