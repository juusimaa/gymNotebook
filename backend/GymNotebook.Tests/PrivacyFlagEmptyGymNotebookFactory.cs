using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// PRIVACY_LIFECYCLE_ENABLED set but empty — what Compose produces for a variable left
// blank in .env. The flag must stay off (plan.md P25: fail closed).
public class PrivacyFlagEmptyGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PRIVACY_LIFECYCLE_ENABLED", "");
    }
}
