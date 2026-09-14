using System.Net;
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

    [Fact]
    public async Task Me_without_a_token_returns_unauthorized()
    {
        var response = await _client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_a_valid_token_returns_the_users_id()
    {
        var username = TestUsers.UniqueUsername();
        var token = await _client.RegisterAsync(username);

        var response = await _client.GetAsync("/auth/me", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();

        // The username is what finds the user's row directly, to compare against the id
        // the endpoint reported.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Username == username);
        Assert.Equal(user.Id, body?.UserId);
    }

    [Fact]
    public async Task Me_with_a_token_from_before_a_token_version_bump_returns_unauthorized()
    {
        var username = TestUsers.UniqueUsername();
        var token = await _client.RegisterAsync(username);

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

        var response = await _client.GetAsync("/auth/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
