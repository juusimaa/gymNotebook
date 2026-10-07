using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// The Turnstile bot check on signup and reset requests (specs/002 FR-019, Story 4 scenario
// 1, tasks.md T051), with Cloudflare stubbed (TurnstileGymNotebookFactory). Each test picks
// what "Cloudflare" answers by the token it sends; see StubSiteverifyHandler.
//
// Register shares the "auth" rate limit (10 a minute per client, and every test here is the
// same client), so the scenarios that are about Turnstile itself rather than about which
// route it guards are tested on /password-reset, whose limit the fixture raises.
public class TurnstileTests(TurnstileGymNotebookFactory factory) : IClassFixture<TurnstileGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // --- /auth/register (action "signup") ---

    [Fact]
    public async Task Register_PassingToken_Returns202AndSendsConfirmation()
    {
        // Arrange
        var email = EmailTestSupport.UniqueEmail();

        // Act
        var response = await RegisterAsync(email, StubSiteverifyHandler.PassSignup);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await EmailTestSupport.WaitForMessageAsync(factory.Services, email, "confirmation");
    }

    [Theory]
    [InlineData(null)]                                // the widget never ran
    [InlineData(StubSiteverifyHandler.Rejected)]      // Cloudflare says no
    [InlineData(StubSiteverifyHandler.OtherHostname)] // solved on someone else's page
    [InlineData(StubSiteverifyHandler.PassReset)]     // solved on the reset form
    public async Task Register_TokenNotForThisForm_Returns400CaptchaAndCreatesNothing(string? token)
    {
        // Arrange
        var email = EmailTestSupport.UniqueEmail();

        // Act
        var response = await RegisterAsync(email, token);

        // Assert
        await AssertCaptchaAsync(response);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    // --- /auth/password-reset (action "password_reset") ---

    [Fact]
    public async Task RequestPasswordReset_PassingToken_Returns202AndSendsLink()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);

        // Act
        var response = await RequestResetAsync(user.Email, StubSiteverifyHandler.PassReset);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await EmailTestSupport.WaitForMessageAsync(factory.Services, user.Email, "password_reset");
    }

    // Cloudflare's always-pass test secret answers with its own hostname and no action;
    // the action check is skipped for those answers only (Turnstile.cs).
    [Fact]
    public async Task RequestPasswordReset_TestKeyAnswer_Returns202()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);

        // Act
        var response = await RequestResetAsync(user.Email, StubSiteverifyHandler.TestKey);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(StubSiteverifyHandler.Rejected)]
    [InlineData(StubSiteverifyHandler.OtherHostname)]
    [InlineData(StubSiteverifyHandler.PassSignup)]   // solved on the signup form
    [InlineData(StubSiteverifyHandler.Unreachable)]  // fails closed
    [InlineData(StubSiteverifyHandler.ServerError)]  // fails closed
    public async Task RequestPasswordReset_TokenNotForThisForm_Returns400CaptchaAndSendsNothing(string? token)
    {
        // Arrange: an account the link would go to if the check let the request through.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);

        // Act
        var response = await RequestResetAsync(user.Email, token);

        // Assert
        await AssertCaptchaAsync(response);
        await EmailTestSupport.DrainOutboxAsync(factory.Services);
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, user.Email));
    }

    // Longer than any real token can be: refused without asking Cloudflare.
    [Fact]
    public async Task RequestPasswordReset_OverlongToken_Returns400WithoutCallingCloudflare()
    {
        // Arrange
        var token = new string('x', 2049);

        // Act
        var response = await RequestResetAsync(EmailTestSupport.UniqueEmail(), token);

        // Assert
        await AssertCaptchaAsync(response);
        Assert.DoesNotContain(factory.SiteverifyRequests, form => form.GetValueOrDefault("response") == token);
    }

    // The check is the server's secret plus the browser's token. Without the secret,
    // Cloudflare can't tell which site is asking.
    [Fact]
    public async Task RequestPasswordReset_PassingToken_SendsSecretAndTokenToCloudflare()
    {
        // Act
        await RequestResetAsync(EmailTestSupport.UniqueEmail(), StubSiteverifyHandler.PassReset);

        // Assert
        Assert.Contains(factory.SiteverifyRequests, form =>
            form.GetValueOrDefault("secret") == TurnstileGymNotebookFactory.SecretKey
            && form.GetValueOrDefault("response") == StubSiteverifyHandler.PassReset);
    }

    // A malformed address is still the same 400 invalid_request as with the check off:
    // the field checks come first, so a typo doesn't spend a token.
    [Fact]
    public async Task RequestPasswordReset_MalformedEmail_Returns400InvalidRequest()
    {
        // Act
        var response = await RequestResetAsync("not-an-address", StubSiteverifyHandler.PassReset);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    private Task<HttpResponseMessage> RegisterAsync(string email, string? token) =>
        _client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, EmailTestSupport.Password, "Test user", token));

    private Task<HttpResponseMessage> RequestResetAsync(string email, string? token) =>
        _client.PostAsJsonAsync("/auth/password-reset", new PasswordResetRequest(email, token));

    private static async Task AssertCaptchaAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("captcha", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }
}

// The default: no TURNSTILE_SECRET_KEY, no check. Local development and every other test
// class run this way, and a missing token must not be refused (the frontend sends none
// when it has no site key).
public class TurnstileOffTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_CheckOffWithoutToken_Returns202()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/auth/register",
            new RegisterRequest(EmailTestSupport.UniqueEmail(), EmailTestSupport.Password, "Test user"));

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task RequestPasswordReset_CheckOffWithoutToken_Returns202()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/auth/password-reset",
            new PasswordResetRequest(EmailTestSupport.UniqueEmail()));

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
