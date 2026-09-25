using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/001 user story 1 (tasks.md T028): the public notice, the account's privacy state
// and the acknowledgement that the notebook gate's "Continue" records. Four fixtures:
// three flag values that must leave the feature off, the flag on with the real embedded
// notices, and the flag on with a test catalog and a movable clock for version changes.

// The feature flag's first behavioural tests. T003 added the flag with no test because
// nothing read it; these prove the exact-match, fail-closed rule (plan.md P25) on the
// routes that now depend on it. The facts live in this abstract base and run once per
// derived class, each with its own host and flag value.
public abstract class PrivacyFlagDisabledTests<TFactory>(TFactory factory) : IClassFixture<TFactory>
    where TFactory : GymNotebookFactory
{
    [Fact]
    public async Task GetNotice_FlagNotExactlyTrue_Returns404()
    {
        // Arrange
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/privacy/notice");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AccountPrivacyRoutes_FlagNotExactlyTrue_Return404()
    {
        // Arrange: a valid token, so a 404 can only mean "not mapped", not "not signed in".
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);

        // Act
        var state = await client.GetAsync("/account/privacy");
        var acknowledge = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest("any"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, acknowledge.StatusCode);
    }
}

// "false": the base factory's value, and production's until T084.
public class PrivacyFlagFalseTests(GymNotebookFactory factory)
    : PrivacyFlagDisabledTests<GymNotebookFactory>(factory);

// "True": wrong case.
public class PrivacyFlagCapitalizedTests(PrivacyFlagCapitalizedGymNotebookFactory factory)
    : PrivacyFlagDisabledTests<PrivacyFlagCapitalizedGymNotebookFactory>(factory);

// "": set but blank.
public class PrivacyFlagEmptyTests(PrivacyFlagEmptyGymNotebookFactory factory)
    : PrivacyFlagDisabledTests<PrivacyFlagEmptyGymNotebookFactory>(factory);

// The flag on, serving the notices embedded from docs/privacy/notices/. Versions are read
// from the catalog rather than hard-coded, so these keep passing when T043 replaces the
// synthetic files with the reviewed notice.
public class PrivacyNoticeTests(PrivacyEnabledGymNotebookFactory factory) : IClassFixture<PrivacyEnabledGymNotebookFactory>
{
    private string CurrentVersion =>
        factory.Services.GetRequiredService<PrivacyNoticeCatalog>().Current(DateTimeOffset.UtcNow).Version;

    [Fact]
    public async Task GetNotice_Anonymous_ReturnsCurrentNotice()
    {
        // Arrange: no Authorization header at all.
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/privacy/notice");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notice = await response.Content.ReadFromJsonAsync<PrivacyNoticeResponse>();
        Assert.NotNull(notice);
        Assert.Equal(CurrentVersion, notice.Version);
        Assert.NotEmpty(notice.Sections);
    }

    [Fact]
    public async Task GetNotice_SignedIn_DoesNotRecordAcknowledgement()
    {
        // Arrange: a signed-in reader, so the notice *could* tell who read it.
        var (client, userId) = await PrivacyTestAccounts.CreateSignedInClientWithIdAsync(factory);

        // Act
        var response = await client.GetAsync("/privacy/notice");

        // Assert: reading is not acknowledging.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await PrivacyTestAccounts.ReadAcknowledgedVersionAsync(factory, userId));
    }

    [Fact]
    public async Task GetAccountPrivacy_WithoutToken_Returns401()
    {
        // Arrange
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/privacy");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAccountPrivacy_NoAcknowledgement_RequiresAcknowledgementAndNoStore()
    {
        // Arrange: a fresh account, as after the migration — acknowledgement null.
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);

        // Act
        var response = await client.GetAsync("/account/privacy");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var state = await response.Content.ReadFromJsonAsync<AccountPrivacyResponse>();
        Assert.NotNull(state);
        Assert.Equal(CurrentVersion, state.CurrentNoticeVersion);
        Assert.Null(state.Acknowledgement);
        Assert.True(state.RequiresAcknowledgement);
    }

    [Fact]
    public async Task Acknowledge_CurrentVersion_RecordsItAndClearsRequirement()
    {
        // Arrange
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(CurrentVersion));
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var acknowledgement = await response.Content.ReadFromJsonAsync<NoticeAcknowledgementResponse>();
        Assert.Equal(CurrentVersion, acknowledgement?.NoticeVersion);
        Assert.NotNull(state);
        Assert.False(state.RequiresAcknowledgement);
        Assert.Equal(acknowledgement, state.Acknowledgement);
    }

    [Fact]
    public async Task Acknowledge_SameVersionTwice_KeepsOriginalTimestamp()
    {
        // Arrange
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);
        var first = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(CurrentVersion));
        var firstBody = await first.Content.ReadFromJsonAsync<NoticeAcknowledgementResponse>();

        // Act
        var second = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(CurrentVersion));

        // Assert: a retry is a success, and it does not move the recorded time.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<NoticeAcknowledgementResponse>();
        Assert.Equal(firstBody, secondBody);
    }

    [Fact]
    public async Task Acknowledge_UnknownVersion_Returns409AndRecordsNothing()
    {
        // Arrange: a version string the catalog has never had — as good as any stale one.
        var (client, userId) = await PrivacyTestAccounts.CreateSignedInClientWithIdAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest("not-a-published-version"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("notice_version_changed", body?.Code);
        Assert.Null(await PrivacyTestAccounts.ReadAcknowledgedVersionAsync(factory, userId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Acknowledge_MissingVersion_Returns400InvalidRequest(string? version)
    {
        // Arrange
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(version));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_request", body?.Code);
    }

    [Fact]
    public async Task Acknowledge_VersionLongerThanColumn_Returns400InvalidRequest()
    {
        // Arrange: one character over the 64 the database column can hold.
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);
        var tooLong = new string('v', PrivacyNoticeCatalog.MaxVersionLength + 1);

        // Act
        var response = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(tooLong));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UsersTable_AfterAcknowledgement_HasNoConsentColumn()
    {
        // Arrange: nothing — this inspects the schema the migrations produced.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Act
        var consentColumns = await db.Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'users' AND column_name LIKE '%consent%'")
            .ToListAsync();

        // Assert: acknowledgement is stored as a version and a time, and there is no
        // consent value anywhere it could have been written to (FR-003, data-model.md).
        Assert.Empty(consentColumns);
    }
}

// Version changes (FR-003, quickstart §1.4): an announced successor is visible before it
// takes effect without changing who must acknowledge; once it takes effect, everyone who
// acknowledged the old version is asked again and the old version is refused.
public class PrivacyNoticeVersionTests(PrivacyNoticeVersionsGymNotebookFactory factory) : IClassFixture<PrivacyNoticeVersionsGymNotebookFactory>
{
    private const string Current = PrivacyNoticeVersionsGymNotebookFactory.CurrentVersion;
    private const string Successor = PrivacyNoticeVersionsGymNotebookFactory.SuccessorVersion;

    // A minute past the successor's effective time.
    private static readonly TimeSpan _afterSwitch = PrivacyNoticeVersionsGymNotebookFactory.SuccessorDelay + TimeSpan.FromMinutes(1);

    [Fact]
    public async Task GetNotice_BeforeSuccessorEffective_AnnouncesSuccessor()
    {
        // Arrange
        factory.Clock.Offset = TimeSpan.Zero;
        var client = factory.CreateClient();

        // Act
        var notice = await client.GetFromJsonAsync<PrivacyNoticeResponse>("/privacy/notice");

        // Assert
        Assert.NotNull(notice);
        Assert.Equal(Current, notice.Version);
        Assert.Equal(Successor, notice.AnnouncedSuccessor?.Version);
    }

    [Fact]
    public async Task GetAccountPrivacy_BeforeSuccessorEffective_CurrentAcknowledgementStillSuffices()
    {
        // Arrange
        factory.Clock.Offset = TimeSpan.Zero;
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);
        await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(Current));

        // Act
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");

        // Assert: announcing a change does not re-open the gate.
        Assert.NotNull(state);
        Assert.Equal(Current, state.CurrentNoticeVersion);
        Assert.False(state.RequiresAcknowledgement);
    }

    [Fact]
    public async Task Acknowledge_AnnouncedSuccessorBeforeEffective_Returns409()
    {
        // Arrange: only the version in effect can be acknowledged, not one still to come.
        factory.Clock.Offset = TimeSpan.Zero;
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(Successor));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetNotice_AfterSuccessorEffective_ServesSuccessorWithNoAnnouncement()
    {
        // Arrange
        factory.Clock.Offset = _afterSwitch;
        var client = factory.CreateClient();

        // Act
        var notice = await client.GetFromJsonAsync<PrivacyNoticeResponse>("/privacy/notice");

        // Assert
        Assert.NotNull(notice);
        Assert.Equal(Successor, notice.Version);
        Assert.Null(notice.AnnouncedSuccessor);
    }

    [Fact]
    public async Task GetAccountPrivacy_OldAcknowledgementAfterSwitch_RequiresAcknowledgementAgain()
    {
        // Arrange: acknowledged while the prior version was current.
        factory.Clock.Offset = TimeSpan.Zero;
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);
        await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(Current));
        factory.Clock.Offset = _afterSwitch;

        // Act
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");

        // Assert: the old acknowledgement is still reported, but no longer suffices.
        Assert.NotNull(state);
        Assert.Equal(Successor, state.CurrentNoticeVersion);
        Assert.Equal(Current, state.Acknowledgement?.NoticeVersion);
        Assert.True(state.RequiresAcknowledgement);
    }

    [Fact]
    public async Task Acknowledge_StaleTabAfterSwitch_Returns409ThenNewVersionSucceeds()
    {
        // Arrange: a tab opened before the switch still shows the prior version.
        factory.Clock.Offset = _afterSwitch;
        var client = await PrivacyTestAccounts.CreateSignedInClientAsync(factory);

        // Act
        var stale = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(Current));
        var fresh = await client.PutAsJsonAsync("/account/privacy/acknowledgement", new AcknowledgeNoticeRequest(Successor));

        // Assert: the stale version is refused; the reloaded one is recorded.
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var acknowledgement = await fresh.Content.ReadFromJsonAsync<NoticeAcknowledgementResponse>();
        Assert.Equal(Successor, acknowledgement?.NoticeVersion);
    }
}

// Seeds accounts directly and mints their tokens, skipping /auth/register: every test in a
// class shares one host and so one auth rate-limit bucket (see GymNotebookFactory).
internal static class PrivacyTestAccounts
{
    public static async Task<HttpClient> CreateSignedInClientAsync(GymNotebookFactory factory) =>
        (await CreateSignedInClientWithIdAsync(factory)).Client;

    public static async Task<(HttpClient Client, int UserId)> CreateSignedInClientWithIdAsync(GymNotebookFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = $"user-{Guid.NewGuid():N}",
            PasswordHash = "not-a-real-hash",
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TwoHostGymNotebookFixture.Token(user.Id));
        return (client, user.Id);
    }

    public static async Task<string?> ReadAcknowledgedVersionAsync(GymNotebookFactory factory, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.AcknowledgedPrivacyNoticeVersion).SingleAsync();
    }
}
