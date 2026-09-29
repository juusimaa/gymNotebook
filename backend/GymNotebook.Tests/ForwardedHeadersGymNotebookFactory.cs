using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// The rate-limited fixture (PermitLimit 2) with FORWARDED_HEADERS_ENABLED on, as in Azure,
// so the "auth" limiter partitions by the X-Forwarded-For client instead of TestServer's
// single connection address.
public class ForwardedHeadersGymNotebookFactory : RateLimitedGymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("FORWARDED_HEADERS_ENABLED", "true");
    }
}
