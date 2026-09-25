using GymNotebook.Api;

namespace GymNotebook.Tests;

// The catalog's own rules, without a host: the embedded notice files load, and which
// version is current moves to the successor exactly at its effective time.
public class PrivacyNoticeCatalogTests
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _switch = _start.AddMonths(1);

    [Fact]
    public void LoadEmbedded_RepositoryNotices_LoadsWithoutError()
    {
        // Arrange: the files in docs/privacy/notices/, embedded by the API's csproj.

        // Act
        var catalog = PrivacyNoticeCatalog.LoadEmbedded();

        // Assert
        Assert.NotEmpty(catalog.Current(DateTimeOffset.UtcNow).Sections);
    }

    [Fact]
    public void Current_JustBeforeEffectiveAt_IsPriorVersionWithSuccessorAnnounced()
    {
        // Arrange
        var catalog = new PrivacyNoticeCatalog(Notice("v1", _start), Notice("v2", _switch));

        // Act
        var now = _switch.AddTicks(-1);

        // Assert
        Assert.Equal("v1", catalog.Current(now).Version);
        Assert.Equal("v2", catalog.AnnouncedSuccessor(now)?.Version);
    }

    [Fact]
    public void Current_AtEffectiveAt_IsSuccessorAndNothingAnnounced()
    {
        // Arrange
        var catalog = new PrivacyNoticeCatalog(Notice("v1", _start), Notice("v2", _switch));

        // Act
        var now = _switch;

        // Assert
        Assert.Equal("v2", catalog.Current(now).Version);
        Assert.Null(catalog.AnnouncedSuccessor(now));
    }

    [Fact]
    public void Constructor_SuccessorNotAfterCurrent_Throws()
    {
        // Arrange, Act and Assert: a successor must take effect later than the current one.
        Assert.Throws<InvalidOperationException>(() =>
            new PrivacyNoticeCatalog(Notice("v1", _switch), Notice("v2", _start)));
    }

    [Fact]
    public void Constructor_NoSections_Throws()
    {
        // Arrange
        var empty = new PrivacyNoticeVersion("v1", _start, _start, "Summary.", []);

        // Act and Assert
        Assert.Throws<InvalidOperationException>(() => new PrivacyNoticeCatalog(empty, null));
    }

    private static PrivacyNoticeVersion Notice(string version, DateTimeOffset effectiveAt) =>
        new(version, effectiveAt, effectiveAt, "Summary.", [new PrivacyNoticeSection("about", "About", ["Text."])]);
}
