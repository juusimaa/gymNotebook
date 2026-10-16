using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api;

// Deletes expired demo accounts (specs/007 research R8). An expired demo is already
// refused on every request (research R2); this only removes its rows, every
// Demo:CleanupIntervalMinutes. Registered whatever DEMO_MODE_ENABLED says, so demos left
// behind when the flag is switched off are still cleaned up. `--migrate` mode returns
// before the host runs, so this never starts there.
public sealed class DemoCleanupWorker(
    IServiceScopeFactory scopeFactory,
    DemoOptions options,
    TimeProvider clock,
    ILogger<DemoCleanupWorker> logger) : BackgroundService
{
    // At most this many accounts per run, so one run stays short; a backlog clears over
    // the next runs.
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first tick comes one interval after startup, not at once: a deploy shouldn't
        // start with a burst of deletions while the new instance warms up.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.CleanupIntervalMinutes), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    // One cleanup run; returns how many accounts it deleted. Public so tests can run it
    // without waiting for the timer.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<int> expired;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            try
            {
                expired = await db.Users.AsNoTracking()
                    .Where(u => u.DemoExpiresAt <= now)
                    .OrderBy(u => u.DemoExpiresAt)
                    .Select(u => u.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);
            }
            catch (Exception ex) when (AccountLifecycle.IsDatabaseFailure(ex))
            {
                // The worker boundary: nothing to answer, and an exception escaping
                // ExecuteAsync would stop the worker for good. The type only — the message
                // isn't ours to vouch for.
                logger.LogError("Demo cleanup could not list expired demos: {Failure}.", ex.GetType().Name);
                return 0;
            }
        }

        var deleted = 0;
        foreach (var userId in expired)
        {
            // A scope per account, as a request would have: each deletion gets a fresh
            // AppDbContext and its own transaction.
            await using var scope = scopeFactory.CreateAsyncScope();
            var deletion = scope.ServiceProvider.GetRequiredService<AccountDeletion>();
            try
            {
                if (await deletion.DeleteExpiredDemoAsync(userId, ct)) deleted++;
            }
            catch (Exception ex) when (AccountLifecycle.IsDatabaseFailure(ex))
            {
                // One account failing doesn't stop the others; it's retried next run.
                logger.LogError("Demo cleanup failed for one account: {Failure}.", ex.GetType().Name);
            }
        }
        return deleted;
    }
}
