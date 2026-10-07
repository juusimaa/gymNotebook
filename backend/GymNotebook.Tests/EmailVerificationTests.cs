using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// Confirming an address and sending the link again (specs/002 Story 1, FR-004, FR-006,
// FR-009, FR-012), and the rule that a link token is never a session. Most tests mint
// their link tokens with JwtTokenFactory, the way the register handler does; the first
// one goes through the inbox end to end.
public class EmailVerificationTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task VerifyEmail_LinkFromSignupEmail_ConfirmsAndAllowsSignIn()
    {
        // Arrange: sign up, then try to sign in before confirming (Story 1, scenarios 1–3).
        var email = EmailTestSupport.UniqueEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await EmailTestSupport.RegisterAsync(_client, email)).StatusCode);
        var tooEarly = await LoginAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, tooEarly.StatusCode);

        // Act: open the link from the email.
        var message = await EmailTestSupport.WaitForMessageAsync(factory.Services, email, "confirmation");
        var response = await VerifyAsync(EmailTestSupport.LinkToken(message));

        // Assert: the address comes back for the sign-in screen to pre-fill, and signing
        // in now works.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(email, (await response.Content.ReadFromJsonAsync<VerifyEmailResponse>())?.Email);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email)).StatusCode);
    }

    [Fact]
    public async Task VerifyEmail_SameLinkTwice_StillConfirmedAndTimeUnchanged()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var token = VerifyToken(user);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(token)).StatusCode);
        var firstConfirmation = (await FindUserAsync(user.Id)).EmailVerifiedAt;

        // Act
        var response = await VerifyAsync(token);

        // Assert: Story 1, scenario 3 — "opening it a second time still says confirmed".
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(firstConfirmation);
        Assert.Equal(firstConfirmation, (await FindUserAsync(user.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task VerifyEmail_ExpiredLink_Returns400Expired()
    {
        // Arrange: minted 49 hours ago, so it ran out an hour ago.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var token = VerifyToken(user, DateTimeOffset.UtcNow.AddHours(-49));

        // Act
        var response = await VerifyAsync(token);

        // Assert: 400, never 401 (FR-009), and nothing changed.
        await AssertLinkErrorAsync(response, "expired");
        Assert.Null((await FindUserAsync(user.Id)).EmailVerifiedAt);
    }

    public static TheoryData<string> BrokenTokens() => new() { "", "not-a-jwt", "a.b.c" };

    [Theory]
    [MemberData(nameof(BrokenTokens))]
    public async Task VerifyEmail_MissingOrMalformedToken_Returns400Invalid(string token)
    {
        await AssertLinkErrorAsync(await VerifyAsync(token), "invalid");
    }

    [Fact]
    public async Task VerifyEmail_TamperedPayload_Returns400Invalid()
    {
        // Arrange: a real token whose payload is swapped for another account's, keeping
        // the original signature.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var victim = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var parts = VerifyToken(user).Split('.');
        var forged = string.Join('.', parts[0], VerifyToken(victim).Split('.')[1], parts[2]);

        // Act
        var response = await VerifyAsync(forged);

        // Assert
        await AssertLinkErrorAsync(response, "invalid");
        Assert.Null((await FindUserAsync(victim.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task VerifyEmail_ResetOrSessionToken_Returns400Invalid()
    {
        // Arrange: tokens for the right account, but minted for other purposes (FR-006).
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var reset = JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);
        var session = EmailTestSupport.SessionToken(user);

        // Act
        var resetResponse = await VerifyAsync(reset);
        var sessionResponse = await VerifyAsync(session);

        // Assert
        await AssertLinkErrorAsync(resetResponse, "invalid");
        await AssertLinkErrorAsync(sessionResponse, "invalid");
        Assert.Null((await FindUserAsync(user.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task VerifyEmail_TokenForDifferentAddressOnSameAccount_Returns400Invalid()
    {
        // Arrange: a token minted for an address the account no longer has — the
        // situation the email claim exists for (plan D2).
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var token = JwtTokenFactory.CreateLinkToken(
            new User { Id = user.Id, Email = "old@example.test", DisplayName = "", PasswordHash = "", PrivacyAccountId = Guid.Empty },
            LinkPurpose.Verify, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);

        // Act
        var response = await VerifyAsync(token);

        // Assert
        await AssertLinkErrorAsync(response, "invalid");
        Assert.Null((await FindUserAsync(user.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task Bearer_LinkTokens_Return401()
    {
        // Arrange: a confirmed account, and both kinds of link token for it. The reset
        // token even carries the account's current "tv", so only the purpose check stops
        // it working as a session.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var now = DateTimeOffset.UtcNow;
        var verify = JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Verify, GymNotebookFactory.JwtSecret, now);
        var reset = JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset, GymNotebookFactory.JwtSecret, now);

        // Act + Assert: FR-006 — neither is accepted as a bearer token.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(verify));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(reset));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(EmailTestSupport.SessionToken(user)));
    }

    [Fact]
    public async Task Bearer_UnconfirmedAccountSessionToken_Returns401()
    {
        // Arrange: no code path mints this — login refuses unconfirmed accounts — but a
        // token like it must not work anyway (FR-004, plan D5).
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act
        var status = await MeStatusAsync(EmailTestSupport.SessionToken(user));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AcquireShared_UnconfirmedAccount_RevokedButExclusiveIsGranted()
    {
        // Arrange: the lifecycle guard's re-check under the lock, below the bearer check.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Act: shared is what a session takes; exclusive is what the password reset
        // (specs/002 PR 4) will take, for accounts that may not be confirmed yet.
        GuardOutcome shared, exclusive;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            shared = await AccountLifecycle.AcquireSharedAsync(db, user.Id, user.TokenVersion, 1000, CancellationToken.None);
        }
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            exclusive = await AccountLifecycle.AcquireExclusiveAsync(db, user.Id, user.TokenVersion, 1000, CancellationToken.None);
        }

        // Assert
        Assert.Equal(GuardOutcome.Revoked, shared);
        Assert.Equal(GuardOutcome.Ok, exclusive);
    }

    [Fact]
    public async Task Verification_UnconfirmedAccountCorrectPassword_SendsNewConfirmationLink()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act: "Send the link again" on the check-your-inbox screen (FR-012).
        var response = await ResendAsync(user.Email.ToUpperInvariant(), EmailTestSupport.Password);

        // Assert: and the new link works.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var message = await EmailTestSupport.WaitForMessageAsync(factory.Services, user.Email, "confirmation");
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(EmailTestSupport.LinkToken(message))).StatusCode);
    }

    [Fact]
    public async Task Verification_WrongPasswordUnknownOrConfirmedAddress_Returns204AndSendsNothing()
    {
        // Arrange
        var unconfirmed = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);
        var confirmed = await EmailTestSupport.SeedUserAsync(factory.Services);
        var unknown = EmailTestSupport.UniqueEmail();

        // Act
        var responses = new[]
        {
            await ResendAsync(unconfirmed.Email, "wrong-password"),
            await ResendAsync(unknown, EmailTestSupport.Password),
            await ResendAsync(confirmed.Email, EmailTestSupport.Password),
        };

        // Assert: the same 204 each time, and no email to any of them.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        await EmailTestSupport.DrainOutboxAsync(factory.Services);
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, unconfirmed.Email));
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, unknown));
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, confirmed.Email));
    }

    private static string VerifyToken(User user, DateTimeOffset? mintedAt = null) =>
        JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Verify, GymNotebookFactory.JwtSecret, mintedAt ?? DateTimeOffset.UtcNow);

    private Task<HttpResponseMessage> VerifyAsync(string token) =>
        _client.PostAsJsonAsync("/auth/verify-email", new VerifyEmailRequest(token));

    private Task<HttpResponseMessage> ResendAsync(string email, string password) =>
        _client.PostAsJsonAsync("/auth/verification", new ResendVerificationRequest(email, password));

    private Task<HttpResponseMessage> LoginAsync(string email) =>
        _client.PostAsJsonAsync("/auth/login", new LoginRequest(email, EmailTestSupport.Password));

    private async Task<HttpStatusCode> MeStatusAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await _client.SendAsync(request)).StatusCode;
    }

    private static async Task AssertLinkErrorAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    private async Task<User> FindUserAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }
}
