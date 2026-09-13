using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

public class AuthTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    [Fact]
    public async Task Register_creates_user_and_returns_token()
    {
        var request = new RegisterRequest(UniqueUsername(), "correct-horse-battery-staple", null);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task Register_with_duplicate_username_returns_conflict()
    {
        var request = new RegisterRequest(UniqueUsername(), "correct-horse-battery-staple", null);
        await _client.PostAsJsonAsync("/auth/register", request);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_blank_username_returns_bad_request()
    {
        var request = new RegisterRequest("   ", "correct-horse-battery-staple", null);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_correct_credentials_returns_token_with_expected_claims()
    {
        var username = UniqueUsername();
        const string password = "correct-horse-battery-staple";
        await _client.PostAsJsonAsync("/auth/register", new RegisterRequest(username, password, null));

        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(username, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);
        Assert.Equal("0", jwt.Claims.Single(c => c.Type == "tv").Value);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_unauthorized()
    {
        var username = UniqueUsername();
        await _client.PostAsJsonAsync("/auth/register", new RegisterRequest(username, "correct-horse-battery-staple", null));

        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(username, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_unknown_username_returns_unauthorized()
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(UniqueUsername(), "whatever"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class InviteCodeGatedRegisterTests(InviteCodeGymNotebookFactory factory) : IClassFixture<InviteCodeGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    [Fact]
    public async Task Register_without_invite_code_is_forbidden()
    {
        var request = new RegisterRequest(UniqueUsername(), "correct-horse-battery-staple", null);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_wrong_invite_code_is_forbidden()
    {
        var request = new RegisterRequest(UniqueUsername(), "correct-horse-battery-staple", "not-the-code");

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_correct_invite_code_succeeds()
    {
        var request = new RegisterRequest(
            UniqueUsername(),
            "correct-horse-battery-staple",
            InviteCodeGymNotebookFactory.RequiredInviteCode);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
