using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// /auth/register and /auth/login, driven through the real pipeline against a real
// Postgres. IClassFixture<T> makes xUnit build one GymNotebookFactory (one container,
// one host) for the whole class and pass it to every test's constructor, instead of
// starting a fresh container per test.
public class AuthTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    // CreateClient() returns an HttpClient wired straight into the in-process TestServer:
    // no port, no network, but the full middleware pipeline.
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_creates_user_and_returns_token()
    {
        var request = new RegisterRequest(TestUsers.UniqueUsername(), TestUsers.DefaultPassword, null);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task Register_with_duplicate_username_returns_conflict()
    {
        var request = new RegisterRequest(TestUsers.UniqueUsername(), TestUsers.DefaultPassword, null);
        await _client.PostAsJsonAsync("/auth/register", request);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_blank_username_returns_bad_request()
    {
        var request = new RegisterRequest("   ", TestUsers.DefaultPassword, null);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_correct_credentials_returns_token_with_expected_claims()
    {
        var username = TestUsers.UniqueUsername();
        await _client.RegisterAsync(username);

        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(username, TestUsers.DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);

        // ReadJwtToken decodes without validating: this checks what JwtTokenFactory wrote
        // into the token, not the signature — the middleware tests in MeTests cover that.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);
        Assert.Equal("0", jwt.Claims.Single(c => c.Type == "tv").Value);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_unauthorized()
    {
        var username = TestUsers.UniqueUsername();
        await _client.RegisterAsync(username);

        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(username, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_unknown_username_returns_unauthorized()
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(TestUsers.UniqueUsername(), "whatever"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// The gated-registration cases. A separate class because they need a different fixture
// (INVITE_CODE set) — see InviteCodeGymNotebookFactory for why that can't be a runtime
// toggle on the shared one.
public class InviteCodeGatedRegisterTests(InviteCodeGymNotebookFactory factory) : IClassFixture<InviteCodeGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_without_invite_code_is_forbidden()
    {
        var request = new RegisterRequest(TestUsers.UniqueUsername(), TestUsers.DefaultPassword, null);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_wrong_invite_code_is_forbidden()
    {
        var request = new RegisterRequest(TestUsers.UniqueUsername(), TestUsers.DefaultPassword, "not-the-code");

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_correct_invite_code_succeeds()
    {
        var request = new RegisterRequest(
            TestUsers.UniqueUsername(),
            TestUsers.DefaultPassword,
            InviteCodeGymNotebookFactory.RequiredInviteCode);

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
