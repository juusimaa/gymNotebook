using System.Security.Claims;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// Backup is available independently of the privacy rollout (specs/004 D2).
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
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
           .WithMetadata(new BackupNoStoreMetadata())
           .RequireRateLimiting("auth")
           .WithMetadata(new SensitiveOperationMetadata())
           .WithName("ExportNotebook")
           .WithTags("Backup")
           .WithSummary("Downloads a copy of the caller's notebook")
           .WithDescription("Verifies the current password, then streams the whole notebook as one JSON file (format version 1) read from a single database snapshot, with an embedded field guide. The stream is cut, never completed, if the account is deleted, the password changed or the token expires during the download. Both formats return JSON; format selects whether completion records the last full-backup time (json, the default) or leaves it unchanged (csv, for browser conversion).")
           .Accepts<ExportRequest>("application/json")
           .Produces(StatusCodes.Status200OK, contentType: "application/json")
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces(StatusCodes.Status415UnsupportedMediaType)
           .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
           .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/account/backup", async (ClaimsPrincipal caller, AppDbContext db, CancellationToken ct) =>
        {
            var (userId, _) = AccountLifecycle.ReadClaims(caller);
            // Authentication may already track an older User. Read the current value
            // under the lifecycle filter's lock, projecting only this account's stamp.
            var backup = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new BackupResponse(u.LastBackupAt))
                .SingleAsync(ct);
            return Results.Ok(backup);
        })
           .RequireAuthorization()
           .WithMetadata(new BackupNoStoreMetadata())
           .RequireAccountLifecycle()
           .WithName("GetBackup")
           .WithTags("Backup")
           .WithSummary("Returns when the caller's last full backup finished sending")
           .Produces<BackupResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}

public record BackupResponse(DateTimeOffset? LastBackupAt);

// Read by middleware before authentication, rate limiting and JSON binding can fail.
internal sealed class BackupNoStoreMetadata;
