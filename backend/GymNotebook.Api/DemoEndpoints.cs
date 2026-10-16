using System.Security.Claims;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymNotebook.Api;

// Demo mode (specs/007): a visitor starts a 24-hour account with a full notebook, and can
// reset it to a fresh one. Mapped only when DEMO_MODE_ENABLED is exactly "true"
// (Program.cs), so both routes 404 otherwise, like any unknown route.
public static class DemoEndpoints
{
    // First key of the demo start's advisory lock, "DEMO" in ASCII so it's recognizable in
    // pg_locks and can't collide with the lifecycle, export or restore namespaces.
    public const int DemoLockNamespace = 0x44454D4F;

    // Retry-After on demo_unavailable: demos expire one by one, so a few minutes is a
    // fair wait before asking again.
    private const string FullRetryAfterSeconds = "300";

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app, string jwtSecret, int jwtExpiryMinutes, bool privacyLifecycleEnabled)
    {
        // Start a demo (research R3). One transaction from the cap check to the last seeded
        // set, so a failure anywhere leaves no half-made account behind (spec edge case).
        app.MapPost("/auth/demo", async (DemoStartRequest request, AppDbContext db, NotebookRestore restore, Turnstile turnstile,
            DemoOptions options, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            if (!IsValidOffset(request.UtcOffsetMinutes))
            {
                return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
            }

            // Fails closed like signup's: nothing is created when Cloudflare says no or
            // can't be reached.
            if (!await turnstile.VerifyAsync(request.TurnstileToken, http.Connection.RemoteIpAddress?.ToString(), Turnstile.DemoAction, ct))
            {
                return Results.Json(new ErrorResponse("captcha"), statusCode: StatusCodes.Status400BadRequest);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // One global lock for every demo start, held to the commit, so two starts can't
            // both count 199 and both insert: the cap is exact. It serializes demo starts
            // only — rare and rate-limited — and no other request ever takes it.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DemoLockNamespace}, 0)", ct);

            var now = clock.GetUtcNow();
            var live = await db.Users.CountAsync(u => u.DemoExpiresAt > now, ct);
            if (live >= options.MaxLiveAccounts)
            {
                http.Response.Headers.RetryAfter = FullRetryAfterSeconds;
                return Results.Json(new ErrorResponse("demo_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // An ordinary account apart from its expiry (research R1, data-model.md).
            var user = new User
            {
                // No "@" on purpose (research R7): no email route accepts it as an address,
                // so nothing can ever be mailed to a demo. Unique like any address.
                Email = "demo:" + Guid.NewGuid(),
                DisplayName = DemoOptions.DisplayName,
                // A password nobody knows: the GUID is thrown away. Cost 4, the minimum,
                // because the hash is never checked — login refuses demos anyway — so the
                // default cost would only slow every start down.
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), 4),
                // The session guards require a confirmed address.
                EmailVerifiedAt = now,
                TokenVersion = 0,
                PrivacyAccountId = Guid.NewGuid(),
                DemoExpiresAt = now.AddHours(options.LifetimeHours),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            await SeedAsync(restore, user.Id, request.UtcOffsetMinutes!.Value, now, privacyLifecycleEnabled, ct);
            await transaction.CommitAsync(ct);

            // The same answer as login, so the frontend's sign-in handling is reused.
            return Results.Ok(new AuthResponse(JwtTokenFactory.CreateToken(user, jwtSecret, jwtExpiryMinutes, now)));
        })
           .AddEndpointFilter(PrivacyEndpoints.NoStore)
           .RequireRateLimiting("demo")
           .WithName("StartDemo")
           .WithTags("Demo")
           .WithSummary("Starts a demo account with a sample notebook")
           .WithDescription("Creates a demo account that expires after Demo:LifetimeHours (24 by default), fills it with about twelve weeks of generated training ending today in the caller's time zone, and returns a session token like login. utcOffsetMinutes is required (−840 to 840). When the Turnstile bot check is configured, a missing or failing turnstileToken is 400 captcha. 503 demo_unavailable when the number of live demo accounts is at Demo:MaxLiveAccounts; nothing is created.")
           .Accepts<DemoStartRequest>("application/json")
           .Produces<AuthResponse>(StatusCodes.Status200OK)
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status429TooManyRequests)
           .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Reset the demo (research R6): the whole notebook replaced by a fresh one; the
        // account, its session, its privacy answers and its expiry kept (FR-010).
        //
        // Not under the shared lifecycle filter (it's on LifecycleCoverageTests' allow-list):
        // like deletion and password change it takes *exclusive* access in a transaction it
        // owns, so no other request of this account can see the notebook half-replaced or
        // save a page between the delete and the import. Asking for exclusive access while
        // the filter held shared access would deadlock two callers (analysis I1).
        app.MapPost("/account/demo/reset", async (DemoResetRequest request, ClaimsPrincipal caller, AppDbContext db, NotebookRestore restore,
            LifecycleOptions lifecycle, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            if (!IsValidOffset(request.UtcOffsetMinutes))
            {
                return Results.Json(new ErrorResponse("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
            }

            var (userId, tokenVersion) = AccountLifecycle.ReadClaims(caller);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var outcome = await AccountLifecycle.AcquireExclusiveAsync(db, userId, tokenVersion, lifecycle.ExclusiveLockTimeoutMs, ct);
            if (outcome != GuardOutcome.Ok)
            {
                return AccountLifecycle.ToResult(outcome, http);
            }

            // Read under the lock, untracked: OnTokenValidated's FindAsync tracks a copy
            // loaded before it.
            var expiresAt = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.DemoExpiresAt)
                .SingleAsync(ct);
            if (expiresAt is null)
            {
                return Results.Json(new ErrorResponse("not_a_demo"), statusCode: StatusCodes.Status403Forbidden);
            }
            var now = clock.GetUtcNow();
            if (expiresAt <= now)
            {
                return Results.Unauthorized();
            }

            try
            {
                await AccountDeletion.DeleteNotebookAsync(db, userId);
                await SeedAsync(restore, userId, request.UtcOffsetMinutes!.Value, now, privacyLifecycleEnabled, ct);
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && AccountLifecycle.IsDatabaseFailure(ex))
            {
                // As in restore: the transaction never committed, so disposing it rolls the
                // delete back and the old notebook is still whole. Safe to retry.
                return AccountLifecycle.TemporarilyUnavailable(http);
            }

            // Nothing personal in the answer, so no delivery guard is needed after the commit.
            return Results.NoContent();
        })
           .RequireAuthorization()
           .AddEndpointFilter(PrivacyEndpoints.NoStore)
           .RequireRateLimiting("restore")
           .WithName("ResetDemo")
           .WithTags("Demo")
           .WithSummary("Replaces the demo account's notebook with a fresh sample")
           .WithDescription("Deletes every workout, template and exercise of the caller's demo account and generates a new sample notebook ending today in the caller's time zone. The session, privacy answers and expiry are kept. Sample titles, locations, notes and bodyweight are included only with optional-details consent (or while the privacy feature is off). 403 not_a_demo for a registered account; 503 temporarily_unavailable when another request of the account held it too long; nothing changes in either case.")
           .Accepts<DemoResetRequest>("application/json")
           .Produces(StatusCodes.Status204NoContent)
           .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
           .Produces(StatusCodes.Status429TooManyRequests)
           .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static bool IsValidOffset(int? minutes) =>
        minutes is >= -DemoOptions.MaxUtcOffsetMinutes and <= DemoOptions.MaxUtcOffsetMinutes;

    // Builds the visitor's notebook and imports it into the caller's transaction. The seed
    // failing restore's validation would be a bug in DemoSeed, never the visitor's doing,
    // so it throws (a 500 that rolls everything back) rather than answering 400.
    private static async Task SeedAsync(NotebookRestore restore, int userId, int utcOffsetMinutes, DateTimeOffset utcNow,
        bool privacyLifecycleEnabled, CancellationToken ct)
    {
        var file = DemoSeed.Build(utcNow.ToOffset(TimeSpan.FromMinutes(utcOffsetMinutes)), new Random());
        var reason = NotebookRestore.Validate(file);
        if (reason is not null)
        {
            throw new InvalidOperationException($"The demo seed failed restore validation: {reason}.");
        }
        await restore.ImportAsync(file, userId, privacyLifecycleEnabled, ct);
    }
}
