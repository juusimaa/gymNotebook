using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// T099's second half: /auth/change-password's update runs in its own exclusive transaction,
// like the deletion, and a transient database failure there (which EF's execution strategy
// hands back wrapped) must answer 503 temporarily_unavailable — nothing changed — rather
// than an unhandled 500. Here, not in ChangePasswordTests, because it needs the lifecycle
// fixture's configurable lock timeout and a direct connection to hold the row.
[Collection("Lifecycle")]
public class ChangePasswordRollbackTests(TwoHostGymNotebookFixture db)
{
    [Fact]
    public async Task ChangePassword_LockTimeoutDuringUpdate_Returns503AndKeepsOldPassword()
    {
        // Arrange: another connection holds the User row, so the UPDATE waits until the
        // guard's 500 ms lock_timeout gives up with 55P03.
        await using var host = db.CreateHost(
            ExportTestSupport.FlagOn(("Lifecycle:ExclusiveLockTimeoutMs", "500"), ("Lifecycle:WriteTimeoutMs", "200")));
        var userId = await db.SeedUserAsync();
        var before = await DeletionTestSupport.SnapshotAccountAsync(db, userId);
        using var client = host.ClientFor(userId);
        HttpResponseMessage response;

        // Act
        await using (await DeletionTestSupport.HoldUserRowLockAsync(db, userId))
        {
            response = await client.PostAsJsonAsync("/auth/change-password",
                new ChangePasswordRequest(TwoHostGymNotebookFixture.Password, "a-new-password-1234"));
        }

        // Assert: safe to retry, the hash and token version exactly as they were, and the
        // existing session still accepted.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(before, await DeletionTestSupport.SnapshotAccountAsync(db, userId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
    }
}
