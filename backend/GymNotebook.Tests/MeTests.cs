using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

public class MeTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    private async Task<(string Token, string Username)> RegisterAndGetTokenAsync()
    {
        var username = UniqueUsername();
        var response = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, "correct-horse-battery-staple", null));
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.Token, username);
    }

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

        // /auth/change-password (PR 4) is what normally bumps this; simulated directly
        // here since that endpoint doesn't exist yet. Proves OnTokenValidated actually
        // rejects a cryptographically valid, unexpired token once token_version moves on.
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
