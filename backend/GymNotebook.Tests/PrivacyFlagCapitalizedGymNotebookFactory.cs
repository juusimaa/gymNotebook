using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// PRIVACY_LIFECYCLE_ENABLED=True — a near miss for the exact value "true". The flag must
// stay off (plan.md P25: only the exact string enables it). Its own factory, and so its
// own host, because the flag is read once at startup.
public class PrivacyFlagCapitalizedGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PRIVACY_LIFECYCLE_ENABLED", "True");
    }
}
