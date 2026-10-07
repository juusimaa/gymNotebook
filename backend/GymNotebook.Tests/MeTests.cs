using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// The bearer middleware, tested through /auth/me — the smallest route that sits behind
// it. Covers the two ways a request fails there (no token, revoked token) and the one
// way it succeeds.
public class MeTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // Signs up through the real flow (register, confirm from the email, sign in) and
    // returns the address too, since the tests below need it to find the user's row
    // directly in the database.
    private async Task<(string Token, string Email)> RegisterAndGetTokenAsync()
    {
        var email = EmailTestSupport.UniqueEmail();
        var token = await EmailTestSupport.RegisterConfirmedAsync(_client, factory.Services, email);
        return (token, email);
    }

    // GetAsync can't take per-request headers, so an authenticated call needs a
    // hand-built request with the bearer token attached.
    private static HttpRequestMessage AuthenticatedGet(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Me_without_a_token_returns_unauthorized()
    {
        var response = await _client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_a_valid_token_returns_the_users_id_display_name_and_email()
    {
        var (token, email) = await RegisterAndGetTokenAsync();

        var response = await _client.SendAsync(AuthenticatedGet("/auth/me", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        Assert.Equal(user.Id, body?.UserId);
        Assert.Equal("Test user", body?.DisplayName);
        Assert.Equal(email, body?.Email);
    }

    [Fact]
    public async Task Me_with_a_token_from_before_a_token_version_bump_returns_unauthorized()
    {
        var (token, email) = await RegisterAndGetTokenAsync();

        // /auth/change-password is what normally bumps this, and ChangePasswordTests
        // covers that path end to end. Bumping the row directly here isolates the
        // middleware: it proves OnTokenValidated rejects a cryptographically valid,
        // unexpired token once token_version moves on, independent of any endpoint.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            user.TokenVersion++;
            await db.SaveChangesAsync();
        }

        var response = await _client.SendAsync(AuthenticatedGet("/auth/me", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
