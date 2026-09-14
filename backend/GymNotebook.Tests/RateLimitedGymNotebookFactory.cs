using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// A tiny limit so a test can exceed it in three requests without needing a huge loop or a
// real wall-clock wait for the window to matter. A long window (well beyond how long three
// requests take to fire) keeps the window from rolling over mid-test, which would let a
// request through that was supposed to be rejected.
public class RateLimitedGymNotebookFactory : GymNotebookFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:PermitLimit", "2");
        builder.UseSetting("RateLimit:WindowSeconds", "60");
    }
}
