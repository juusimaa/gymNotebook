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

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    // Returns the username too, since the tests below need it to find the user's row
    // directly in the database.
    private async Task<(string Token, string Username)> RegisterAndGetTokenAsync()
    {
        var username = UniqueUsername();
        var response = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, "correct-horse-battery-staple", null));
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.Token, username);
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
    public async Task Me_with_a_valid_token_returns_the_users_id()
    {
        var (token, username) = await RegisterAndGetTokenAsync();

        var response = await _client.SendAsync(AuthenticatedGet("/auth/me", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Username == username);
        Assert.Equal(user.Id, body?.UserId);
    }

    [Fact]
    public async Task Me_with_a_token_from_before_a_token_version_bump_returns_unauthorized()
    {
        var (token, username) = await RegisterAndGetTokenAsync();

        // /auth/change-password is what normally bumps this, and ChangePasswordTests
        // covers that path end to end. Bumping the row directly here isolates the
        // middleware: it proves OnTokenValidated rejects a cryptographically valid,
        // unexpired token once token_version moves on, independent of any endpoint.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Username == username);
            user.TokenVersion++;
            await db.SaveChangesAsync();
        }

        var response = await _client.SendAsync(AuthenticatedGet("/auth/me", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
