namespace GymNotebook.Api;

// Demo mode's settings (specs/007 research R3), bound from the "Demo" configuration
// section. The defaults are what production runs with; tests shrink or widen them. Read at
// boot even while DEMO_MODE_ENABLED is off, because DemoCleanupWorker always runs: demos
// left behind when the flag is switched off must still be cleaned up.
public sealed class DemoOptions
{
    // The name on every demo notebook's cover.
    public const string DisplayName = "Demo Lifter";

    // The widest real UTC offsets are −12:00 and +14:00; ±14 h bounds both and rejects
    // anything that can't be a browser's getTimezoneOffset().
    public const int MaxUtcOffsetMinutes = 840;

    // How long a demo account lives from its creation. Never extended, not even by a reset.
    public int LifetimeHours { get; set; } = 24;

    // How many unexpired demos may exist at once. A start at the cap answers 503
    // demo_unavailable: the service is full, the visitor did nothing wrong.
    public int MaxLiveAccounts { get; set; } = 200;

    // How often DemoCleanupWorker looks for expired demos. An expired demo is refused at
    // once whatever this says (research R2); this only bounds how long its rows linger.
    public int CleanupIntervalMinutes { get; set; } = 15;

    // At most a day: rows shouldn't linger longer, and PeriodicTimer refuses periods over
    // about 49 days, which would stop the worker — and with it the whole host — at boot.
    public const int MaxCleanupIntervalMinutes = 24 * 60;

    public void Validate()
    {
        if (LifetimeHours <= 0 || MaxLiveAccounts <= 0 || CleanupIntervalMinutes <= 0)
        {
            throw new InvalidOperationException("Demo:LifetimeHours, Demo:MaxLiveAccounts and Demo:CleanupIntervalMinutes must be positive.");
        }
        if (CleanupIntervalMinutes > MaxCleanupIntervalMinutes)
        {
            throw new InvalidOperationException("Demo:CleanupIntervalMinutes must be at most 1440 (a day).");
        }
    }
}
