using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// Boots the app with the privacy and account lifecycle feature switched on (specs/001,
// P25). A separate fixture for the same reason as InviteCodeGymNotebookFactory: flipping
// the flag on the shared GymNotebookFactory would race the tests that expect it off.
public class PrivacyEnabledGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PRIVACY_LIFECYCLE_ENABLED", "true");
    }
}
