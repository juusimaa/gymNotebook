using GymNotebook.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// The privacy feature on, with a test-owned notice catalog and a clock the tests can move,
// so a successor's switch-over (FR-003) can be tested without depending on the dates in
// the synthetic docs/privacy/notices/ files, which T043 will replace.
//
// The successor takes effect ten minutes after the host starts. Tests move time with
// Clock.Offset rather than jumping to a fixed date: whether or not the JWT handler also
// reads the DI TimeProvider, eleven minutes ahead stays inside a token's 30-minute life.
public class PrivacyNoticeVersionsGymNotebookFactory : PrivacyEnabledGymNotebookFactory
{
    public const string CurrentVersion = "test-current";
    public const string SuccessorVersion = "test-successor";

    public static readonly TimeSpan SuccessorDelay = TimeSpan.FromMinutes(10);

    public OffsetTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var start = TimeProvider.System.GetUtcNow();
        var catalog = new PrivacyNoticeCatalog(
            Notice(CurrentVersion, start.AddDays(-30)),
            Notice(SuccessorVersion, start + SuccessorDelay));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(catalog);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    private static PrivacyNoticeVersion Notice(string version, DateTimeOffset effectiveAt) =>
        new(version, effectiveAt, effectiveAt, $"Test notice {version}.",
            [new PrivacyNoticeSection("about", "About", [$"Test notice {version}."])]);
}

// Real time plus an adjustable offset: time keeps moving, and a test can jump it forward.
public sealed class OffsetTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}
