using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Tests;

// The reset's exclusive access (specs/002 plan D3) against the other lifecycle
// operations, pinned deterministically: the test holds exclusive access itself, standing
// in for a deletion or another password change, while the reset waits on it.
[Collection("Lifecycle")]
public class PasswordResetRaceTests(TwoHostGymNotebookFixture db)
{
    private const string NewPassword = "a-brand-new-password-5678";

    [Fact]
    public async Task ConfirmPasswordReset_WhileAccountLocked_Returns503AndChangesNothing()
    {
        // Arrange: exclusive access held past the reset's 500 ms wait.
        await using var host = db.CreateHost(new Dictionary<string, string?>
        {
            ["Lifecycle:ExclusiveLockTimeoutMs"] = "500",
            ["Lifecycle:WriteTimeoutMs"] = "200",
        });
        var user = await FindUserAsync(await db.SeedUserAsync());
        using var client = host.CreateClient();
        HttpResponseMessage response;

        // Act
        await using (await db.HoldExclusiveAsync(user.Id))
        {
            response = await ConfirmAsync(client, ResetToken(user));
        }

        // Assert: safe to retry, and the link still works once the account is free.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var after = await FindUserAsync(user.Id);
        Assert.Equal(user.PasswordHash, after.PasswordHash);
        Assert.Equal(user.TokenVersion, after.TokenVersion);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, ResetToken(user))).StatusCode);
    }

    [Fact]
    public async Task ConfirmPasswordReset_PasswordChangeCommitsFirst_Returns400InvalidAndKeepsThatChange()
    {
        // Arrange: the reset has passed its unlocked look and is waiting on the lock while
        // a password change holds it.
        await using var host = db.CreateHost();
        var user = await FindUserAsync(await db.SeedUserAsync());
        using var client = host.CreateClient();
        await using var change = await db.HoldExclusiveAsync(user.Id);
        var reset = ConfirmAsync(client, ResetToken(user));
        await db.WaitForLockWaitersAsync(user.Id, 1);

        // Act: the change commits first, so under the lock the link's "tv" is stale.
        await change.BumpTokenVersionAndCommitAsync();
        var response = await reset;

        // Assert: the link has nothing more to give — 400, not 401 (FR-009) — and the
        // reset wrote nothing over the change.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        var after = await FindUserAsync(user.Id);
        Assert.Equal(user.PasswordHash, after.PasswordHash);
        Assert.Equal(user.TokenVersion + 1, after.TokenVersion);
    }

    private static string ResetToken(User user) =>
        JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string token) =>
        client.PostAsJsonAsync("/auth/password-reset/confirm", new ConfirmPasswordResetRequest(token, NewPassword));

    private async Task<User> FindUserAsync(int userId)
    {
        await using var context = db.NewContext();
        return await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }
}
