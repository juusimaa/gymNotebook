using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// The two email rate-limit policies (specs/002 plan D12) shrunk to two requests, like
// RateLimitedGymNotebookFactory does for "auth", so a test exceeds each in three requests.
// The hour-long production window is kept: far longer than the test takes.
public class EmailRateLimitedGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("EmailRequestRateLimit:PermitLimit", "2");
        builder.UseSetting("EmailLinkRateLimit:PermitLimit", "2");
    }
}
