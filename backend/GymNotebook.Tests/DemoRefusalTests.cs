using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// specs/007 user story 4 (research R7): a demo account can't sign in with a password,
// change one, export, delete itself, or have anything emailed to its address.
public class DemoRefusalTests(DemoEnabledGymNotebookFactory factory) : IClassFixture<DemoEnabledGymNotebookFactory>
{
    private const string AnyPassword = "any-password-at-all-123";

    [Fact]
    public async Task Login_DemoAddress_Returns401()
    {
        // Arrange
        var demo = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(demo);
        var email = (await DemoTestSupport.MeAsync(demo)).Email;

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(email, AnyPassword));

        // Assert: the same bare 401 as a wrong password.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangePassword_Demo_Returns403DemoAccount()
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);

        // Act
        var response = await client.PostAsJsonAsync("/auth/change-password", new ChangePasswordRequest(AnyPassword, "a-new-password-456"));

        // Assert
        await AssertDemoAccountAsync(response);
    }

    [Fact]
    public async Task Export_Demo_Returns403DemoAccount()
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);

        // Act
        var response = await client.PostAsJsonAsync("/account/export", new ExportRequest(AnyPassword));

        // Assert
        await AssertDemoAccountAsync(response);
    }

    [Fact]
    public async Task Register_DemoAddress_Returns400AndQueuesNoEmail()
    {
        // Arrange
        var demo = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(demo);
        var email = (await DemoTestSupport.MeAsync(demo)).Email;

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register", new RegisterRequest(email, AnyPassword, "Someone"));

        // Assert: no "@", so not an address at all.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await EmailTestSupport.DrainOutboxAsync(factory.Services);
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, email));
    }

    [Fact]
    public async Task PasswordReset_DemoAddress_Returns400AndQueuesNoEmail()
    {
        // Arrange
        var demo = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(demo);
        var email = (await DemoTestSupport.MeAsync(demo)).Email;

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/password-reset", new PasswordResetRequest(email));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await EmailTestSupport.DrainOutboxAsync(factory.Services);
        Assert.Empty(EmailTestSupport.MessagesTo(factory.Services, email));
    }

    internal static async Task AssertDemoAccountAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("demo_account", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }
}

// Account deletion is mapped only with the privacy feature on.
public class DemoDeleteRefusalTests(DemoPrivacyEnabledGymNotebookFactory factory) : IClassFixture<DemoPrivacyEnabledGymNotebookFactory>
{
    [Fact]
    public async Task DeleteAccount_Demo_Returns403DemoAccount()
    {
        // Arrange
        var client = factory.CreateClient();
        await DemoTestSupport.StartDemoAsync(client);

        // Act
        var response = await client.PostAsJsonAsync("/account/delete", new DeleteAccountRequest("any-password-at-all-123", ConfirmDeletion: true));

        // Assert: refused, and the demo still works.
        await DemoRefusalTests.AssertDemoAccountAsync(response);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
    }
}
