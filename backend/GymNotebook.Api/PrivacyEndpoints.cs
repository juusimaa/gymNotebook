using System.Security.Claims;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// The privacy notice routes of specs/001 user story 1 (contracts/api.md). Program.cs maps
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

        // The account's own privacy state. A group of its own, "/account/privacy" rather
        // than "/account": the export and delete routes US3/US4 add under /account take
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

        accountPrivacy.MapGet("", async (ClaimsPrincipal caller, AppDbContext db, PrivacyNoticeCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            var (userId, _) = AccountLifecycle.ReadClaims(caller);
            var current = catalog.Current(clock.GetUtcNow());
            var acknowledgement = await ReadAcknowledgementAsync(db, userId, ct);

            // Equality, never ordering: version strings are identifiers, and "later" is
            // decided by the catalog's effective dates, not by comparing text (data-model.md).
            return Results.Ok(new AccountPrivacyResponse(
                current.Version,
                acknowledgement,
                acknowledgement?.NoticeVersion != current.Version));
        })
           .WithName("GetAccountPrivacy")
           .WithSummary("Returns the caller's privacy notice state")
           .WithDescription("The current notice version, the caller's latest acknowledgement (or null), and whether the notebook gate must show the notice before notebook access.")
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

    // Personal responses — and the errors on those routes — must not be stored by the
    // browser or any cache in between (contracts/api.md → Common behavior). Set before
    // the handler runs, so it applies whatever result comes back.
    private static ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return next(context);
    }
}
