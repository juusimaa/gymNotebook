using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Tests;

// Research R4 → Q6: login takes no lifecycle guard. A token issued while a deletion or a
// password change is committing is harmless because every guarded request re-checks the
// account and token version — so that token gets 401 on first use. These tests pin the
// race deterministically: the "deletion"/"password change" holds exclusive access while
// the login runs, and commits only after the token has been issued.
[Collection("Lifecycle")]
public class LoginRaceTests(TwoHostGymNotebookFixture db)
{
    [Fact]
    public async Task Login_WhileDeletionCommits_TokenGets401OnFirstGuardedRequest()
    {
        // Arrange
        await using var host = db.CreateHost();
        var (userId, username) = await SeedAsync();
        using var client = host.CreateClient();

        // Act: login completes while exclusive access is held (it takes no guard)...
        await using var deletion = await db.HoldExclusiveAsync(userId);
        var token = await LoginAsync(client, username);
        // ...then the deletion commits, and the fresh token is used.
        await deletion.DeleteUserAndCommitAsync();
        var me = await MeAsync(client, token);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Login_WhilePasswordChangeCommits_TokenGets401OnFirstGuardedRequest()
    {
        // Arrange
        await using var host = db.CreateHost();
        var (userId, username) = await SeedAsync();
        using var client = host.CreateClient();

        // Act: the token carries the pre-change version; the change then commits.
        await using var change = await db.HoldExclusiveAsync(userId);
        var token = await LoginAsync(client, username);
        await change.BumpTokenVersionAndCommitAsync();
        var me = await MeAsync(client, token);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private async Task<(int UserId, string Username)> SeedAsync()
    {
        var userId = await db.SeedUserAsync();
        await using var context = db.NewContext();
        var username = await context.Users.Where(u => u.Id == userId).Select(u => u.Username).SingleAsync();
        return (userId, username);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(username, TwoHostGymNotebookFixture.Password))
            .WaitAsync(TimeSpan.FromSeconds(10)); // a login that waited on the lock would hang here
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> MeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
