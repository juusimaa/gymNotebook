using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// POST /auth/login by email (specs/002 Story 2, FR-003), driven through the real pipeline
// against a real Postgres. IClassFixture<T> makes xUnit build one GymNotebookFactory (one
// container, one host) for the whole class and pass it to every test's constructor,
// instead of starting a fresh container per test. Accounts are seeded directly: the class
// makes eight logins, close to the shared "auth" bucket's ten per minute already.
public class LoginTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Login_ConfirmedAccountAddressInAnyCaseWithSpaces_ReturnsTokenWithExpectedClaims()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);

        // Act: Story 2, scenario 1.
        var response = await _client.PostAsJsonAsync("/auth/login",
            new LoginRequest($" {user.Email.ToUpperInvariant()}  ", EmailTestSupport.Password));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();

        // ReadJwtToken decodes without validating: this checks what JwtTokenFactory wrote
        // into the token, not the signature — the middleware tests in MeTests cover that.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal("0", jwt.Claims.Single(c => c.Type == "tv").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == JwtTokenFactory.PurposeClaim);
    }

    [Fact]
    public async Task Login_UnknownAddressAndWrongPassword_SameBare401()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);

        // Act: Story 2, scenario 2.
        var wrongPassword = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(user.Email, "wrong-password"));
        var unknown = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(EmailTestSupport.UniqueEmail(), "wrong-password"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("", await wrongPassword.Content.ReadAsStringAsync());
        Assert.Equal("", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_UnconfirmedAccountCorrectPassword_Returns403EmailNotVerified()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act: Story 1, scenario 2.
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(user.Email, EmailTestSupport.Password));

        // Assert: a code the UI turns into "check your inbox", and no token.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("email_not_verified", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Login_UnconfirmedAccountWrongPassword_ReturnsBare401()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(user.Email, "wrong-password"));

        // Assert: "unconfirmed" is revealed only after the right password.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_PasswordLongerThan72BytesWithMatchingPrefix_Returns401()
    {
        // Arrange: BCrypt reads only the first 72 bytes, so without the length rule a
        // password with extra characters after the real one would also sign in.
        var password = new string('p', 72);
        var user = await EmailTestSupport.SeedUserAsync(factory.Services, password: password);

        // Act
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(user.Email, password + "extra"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_BlankEmail_Returns400InvalidRequest()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest("  ", "whatever"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }
}
