using GymNotebook.Api;
using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// Boots the app with demo mode switched on (specs/007). A separate fixture for the same
// reason as PrivacyEnabledGymNotebookFactory: the shared GymNotebookFactory keeps the
// flag off for every other test.
public class DemoEnabledGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        ApplyDemoSettings(builder);
    }

    // The flag, a per-IP limit no test class reaches (every test shares one host and so
    // one bucket; DemoRateLimitedGymNotebookFactory tests the limit itself), and a cleanup
    // interval (the longest allowed, a day) long enough that the worker never runs on its own during a test — tests
    // that need a run call DemoCleanupWorker.RunOnceAsync.
    public static void ApplyDemoSettings(IWebHostBuilder builder)
    {
        builder.UseSetting("DEMO_MODE_ENABLED", "true");
        builder.UseSetting("DemoRateLimit:PermitLimit", "1000");
        builder.UseSetting("Demo:CleanupIntervalMinutes", DemoOptions.MaxCleanupIntervalMinutes.ToString());
    }

    // The same settings for a LifecycleTestHost on the shared lifecycle database.
    public static Dictionary<string, string?> HostSettings(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DEMO_MODE_ENABLED"] = "true",
            ["DemoRateLimit:PermitLimit"] = "1000",
            ["Demo:CleanupIntervalMinutes"] = DemoOptions.MaxCleanupIntervalMinutes.ToString(),
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }
        return settings;
    }
}

// Demo mode and the privacy feature both on: the optional-details consent decides
// whether seeded details are kept (FR-016), and account deletion is mapped. Also the host
// LifecycleCoverageTests enumerates, so the demo reset route is checked too.
public class DemoPrivacyEnabledGymNotebookFactory : PrivacyEnabledGymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        DemoEnabledGymNotebookFactory.ApplyDemoSettings(builder);
    }
}

// A cap of one live demo, so the second start meets it.
public class DemoCapOneGymNotebookFactory : DemoEnabledGymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:MaxLiveAccounts", "1");
    }
}

// Two starts per IP per window, so the third is refused; the window is long enough not to
// roll over mid-test.
public class DemoRateLimitedGymNotebookFactory : DemoEnabledGymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("DemoRateLimit:PermitLimit", "2");
        builder.UseSetting("DemoRateLimit:WindowSeconds", "3600");
    }
}

// "True" is not "true": the exact-string rule keeps demo mode off.
public class DemoFlagCapitalizedGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("DEMO_MODE_ENABLED", "True");
    }
}

// Demo mode with the Turnstile check on, Cloudflare replaced by the stub.
public class DemoTurnstileGymNotebookFactory : TurnstileGymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        DemoEnabledGymNotebookFactory.ApplyDemoSettings(builder);
    }
}
