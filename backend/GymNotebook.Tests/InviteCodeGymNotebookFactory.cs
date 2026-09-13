using Microsoft.AspNetCore.Hosting;

namespace GymNotebook.Tests;

// A separate fixture (own Postgres container, own host) rather than mutating
// GymNotebookFactory's INVITE_CODE mid-suite: xUnit can run tests within a class in
// parallel, so a shared mutable setting would race between the open-registration tests
// and these gated ones.
public class InviteCodeGymNotebookFactory : GymNotebookFactory
{
    public const string RequiredInviteCode = "test-invite-code";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("INVITE_CODE", RequiredInviteCode);
    }
}
