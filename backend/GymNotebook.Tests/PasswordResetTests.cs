using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// "Forgot your password?" and the reset link (specs/002 Story 3, FR-006–FR-009, FR-013).
// The first test goes through the inbox end to end; the rest mint their reset tokens with
// JwtTokenFactory, the way the request handler does.
public class PasswordResetTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private const string NewPassword = "a-brand-new-password-5678";

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ConfirmPasswordReset_LinkFromResetEmail_SignsInWithNewPassword()
    {
        // Arrange: ask for a link the way the Forgot form does (Story 3, scenarios 1–2).
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestResetAsync(user.Email.ToUpperInvariant())).StatusCode);
        var message = await EmailTestSupport.WaitForMessageAsync(factory.Services, user.Email, "password_reset");

        // Act
        var response = await ConfirmAsync(EmailTestSupport.LinkToken(message), NewPassword);

        // Assert: a working session comes back, the new password signs in, the old doesn't.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var token = (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(token));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email, EmailTestSupport.Password)).StatusCode);
    }

    [Fact]
    public async Task RequestPasswordReset_KnownAndUnknownAddress_SameAnswerMailOnlyToKnown()
    {
        // Arrange
        var known = await EmailTestSupport.SeedUserAsync(factory.Services);
        var unknown = EmailTestSupport.UniqueEmail();

        // Act
        var knownResponse = await RequestResetAsync(known.Email);
        var unknownResponse = await RequestResetAsync(unknown);

        // Assert: identical status and body (FR-013); the difference is only in the inbox.
        Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
        Assert.Equal(await knownResponse.Content.ReadAsStringAsync(), await unknownResponse.Content.ReadAsStringAsync());
        Assert.Equal("no-store", unknownResponse.Headers.CacheControl?.ToString());
        await EmailTestSupport.WaitForMessageAsync(factory.Services, known.Email, "password_reset");
        await EmailTestSupport.DrainOutboxAsync(factory.Services);
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, unknown));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    public async Task RequestPasswordReset_MalformedAddress_Returns400(string email)
    {
        var response = await RequestResetAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task ConfirmPasswordReset_ValidLink_RevokesExistingSessions()
    {
        // Arrange: a session in another browser, still working before the reset.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var otherSession = EmailTestSupport.SessionToken(user);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(otherSession));

        // Act
        var response = await ConfirmAsync(ResetToken(user), NewPassword);

        // Assert: FR-007 — every session issued before the reset is signed out.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(otherSession));
    }

    [Fact]
    public async Task ConfirmPasswordReset_SameLinkTwice_SecondIsInvalidAndPasswordKept()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = ResetToken(user);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(token, NewPassword)).StatusCode);

        // Act: someone with the same link tries to set yet another password.
        var response = await ConfirmAsync(token, "someone-elses-password-9");

        // Assert: FR-007 — a link works once, and the first reset stands.
        await AssertLinkErrorAsync(response, "invalid");
        Assert.True(await PasswordIsAsync(user.Id, NewPassword));
    }

    [Fact]
    public async Task ConfirmPasswordReset_UnconfirmedAccount_ConfirmsAddress()
    {
        // Arrange: the finish-signup email's link is a reset link to an unconfirmed account.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act
        var response = await ConfirmAsync(ResetToken(user), NewPassword);

        // Assert: FR-008 — the address now counts as confirmed, so the returned session
        // works (a session for an unconfirmed account would be refused).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await FindUserAsync(user.Id)).EmailVerifiedAt);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync((await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token));
        Assert.True(await PasswordIsAsync(user.Id, NewPassword));
    }

    [Fact]
    public async Task ConfirmPasswordReset_AlreadyConfirmedAccount_KeepsConfirmationTime()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var confirmedAt = (await FindUserAsync(user.Id)).EmailVerifiedAt;

        // Act
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ResetToken(user), NewPassword)).StatusCode);

        // Assert: "set once, never cleared" (data-model.md) — and never moved either.
        Assert.Equal(confirmedAt, (await FindUserAsync(user.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task ConfirmPasswordReset_SuspendedAccount_Returns403AndChangesNothing()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        await SuspendAsync(user.Id);
        var before = await FindUserAsync(user.Id);

        // Act
        var response = await ConfirmAsync(ResetToken(user), NewPassword);

        // Assert: Story 3, scenario 4 — the suspension answer, and no change at all.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("account_suspended", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        var after = await FindUserAsync(user.Id);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.TokenVersion, after.TokenVersion);
    }

    [Fact]
    public async Task ConfirmPasswordReset_ExpiredLink_Returns400Expired()
    {
        // Arrange: minted 61 minutes ago; reset links last an hour.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = ResetToken(user, DateTimeOffset.UtcNow.AddMinutes(-61));

        // Act
        var response = await ConfirmAsync(token, NewPassword);

        // Assert: 400, never 401 (FR-009), and the password unchanged.
        await AssertLinkErrorAsync(response, "expired");
        Assert.True(await PasswordIsAsync(user.Id, EmailTestSupport.Password));
    }

    public static TheoryData<string> BrokenTokens() => new() { "", "not-a-jwt", "a.b.c" };

    [Theory]
    [MemberData(nameof(BrokenTokens))]
    public async Task ConfirmPasswordReset_MissingOrMalformedToken_Returns400Invalid(string token)
    {
        await AssertLinkErrorAsync(await ConfirmAsync(token, NewPassword), "invalid");
    }

    [Fact]
    public async Task ConfirmPasswordReset_TamperedPayload_Returns400Invalid()
    {
        // Arrange: one account's signature on another account's payload.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var victim = await EmailTestSupport.SeedUserAsync(factory.Services);
        var parts = ResetToken(user).Split('.');
        var forged = string.Join('.', parts[0], ResetToken(victim).Split('.')[1], parts[2]);

        // Act
        var response = await ConfirmAsync(forged, NewPassword);

        // Assert
        await AssertLinkErrorAsync(response, "invalid");
        Assert.True(await PasswordIsAsync(victim.Id, EmailTestSupport.Password));
    }

    [Fact]
    public async Task ConfirmPasswordReset_VerifyOrSessionToken_Returns400Invalid()
    {
        // Arrange: tokens for the right account, minted for other purposes (FR-006). The
        // session token even carries the current "tv".
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var verify = JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Verify, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);
        var session = EmailTestSupport.SessionToken(user);

        // Act
        var verifyResponse = await ConfirmAsync(verify, NewPassword);
        var sessionResponse = await ConfirmAsync(session, NewPassword);

        // Assert
        await AssertLinkErrorAsync(verifyResponse, "invalid");
        await AssertLinkErrorAsync(sessionResponse, "invalid");
        Assert.True(await PasswordIsAsync(user.Id, EmailTestSupport.Password));
    }

    [Fact]
    public async Task ConfirmPasswordReset_LinkFromBeforePasswordChange_Returns400Invalid()
    {
        // Arrange: a link minted, then the password changed some other way (here, another
        // reset) — the version bump makes the older link stale too.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var older = ResetToken(user);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ResetToken(user), NewPassword)).StatusCode);

        // Act
        var response = await ConfirmAsync(older, "yet-another-password-1");

        // Assert
        await AssertLinkErrorAsync(response, "invalid");
    }

    public static TheoryData<string?> BadPasswords() => new() { null, "", "   ", new string('a', AccountInput.PasswordMaxBytes + 1) };

    [Theory]
    [MemberData(nameof(BadPasswords))]
    public async Task ConfirmPasswordReset_PasswordBreaksRules_Returns400AndLinkStillWorks(string? password)
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = ResetToken(user);

        // Act
        var response = await ConfirmAsync(token, password);

        // Assert: the same rules as signup (FR-005), and nothing spent — the user can fix
        // the password and submit the same link again.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task ConfirmPasswordReset_SameIdDifferentAddress_Returns400InvalidAndChangesNothing()
    {
        // Arrange: a link for this account's id and current version, but minted for
        // another address — what a link sent before a restore reused the id would look
        // like to the account that now has it.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = JwtTokenFactory.CreateLinkToken(
            new User { Id = user.Id, Email = "previous-owner@example.test", DisplayName = "", PasswordHash = "", TokenVersion = user.TokenVersion, PrivacyAccountId = Guid.Empty },
            LinkPurpose.Reset, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);

        // Act
        var response = await ConfirmAsync(token, NewPassword);

        // Assert
        await AssertLinkErrorAsync(response, "invalid");
        Assert.True(await PasswordIsAsync(user.Id, EmailTestSupport.Password));
    }

    [Fact]
    public async Task ConfirmPasswordReset_DeletedAccount_Returns400Invalid()
    {
        // Arrange: the link outlives its account.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = ResetToken(user);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }

        // Act + Assert
        await AssertLinkErrorAsync(await ConfirmAsync(token, NewPassword), "invalid");
    }

    private static string ResetToken(User user, DateTimeOffset? mintedAt = null) =>
        JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset, GymNotebookFactory.JwtSecret, mintedAt ?? DateTimeOffset.UtcNow);

    private Task<HttpResponseMessage> RequestResetAsync(string email) =>
        _client.PostAsJsonAsync("/auth/password-reset", new PasswordResetRequest(email));

    private Task<HttpResponseMessage> ConfirmAsync(string token, string? newPassword) =>
        _client.PostAsJsonAsync("/auth/password-reset/confirm", new ConfirmPasswordResetRequest(token, newPassword));

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

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

    // The stored hash checked directly, rather than by signing in: every login spends the
    // "auth" rate-limit bucket (10 a minute per IP) that this whole class shares.
    private async Task<bool> PasswordIsAsync(int userId, string password) =>
        BCrypt.Net.BCrypt.Verify(password, (await FindUserAsync(userId)).PasswordHash);

    private async Task SuspendAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SignInSuspendedAt, DateTimeOffset.UtcNow));
    }

    private async Task<User> FindUserAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }
}
