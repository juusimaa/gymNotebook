using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/001 user story 6 (tasks.md T087): a workout save racing a consent withdrawal ends
// either rejected or cleared, never with a detail stored after the withdrawal commits
// (spec edge case "A second tab ... still holding details after withdrawal").
//
// Both run under *shared* lifecycle access, so the advisory lock doesn't order them. The
// users row does: a save that stores a detail reads the consent with FOR SHARE, and the
// withdrawal clears the consent pair (an UPDATE of that row) before it clears the workouts.
// Each test holds one side open on its own connection, so the interleaving is forced
// rather than hoped for, and pg_blocking_pids proves the other side is waiting.
[Collection("Lifecycle")]
public class OptionalDetailsCoordinationTests(TwoHostGymNotebookFixture db)
{
    private static readonly Dictionary<string, string?> _flagOn = new() { ["PRIVACY_LIFECYCLE_ENABLED"] = "true" };

    [Fact]
    public async Task CreateWorkout_WaitedBehindCommittedWithdrawal_Returns403AndStoresNothing()
    {
        // Arrange: an account with consent, and a withdrawal (the same two statements, in
        // the same order, as the endpoint's) held open before it commits.
        await using var host = db.CreateHost(_flagOn);
        var userId = await db.SeedUserAsync();
        await GrantConsentAsync(host, userId);
        using var client = host.ClientFor(userId);
        await using var withdrawal = await db.BeginHeldTransactionAsync(userId);
        await withdrawal.ExecuteAsync("UPDATE users SET optional_details_consent_version = NULL, optional_details_consented_at = NULL WHERE id = @id");
        await withdrawal.ExecuteAsync("UPDATE workouts SET title = NULL, location = NULL, notes = NULL, bodyweight_kg = NULL WHERE user_id = @id");

        // Act: a stale tab saves a note while the withdrawal is in flight; then it commits.
        var pending = client.PostAsync("/workouts", OptionalDetailsEnforcementTests.Json(
            """{ "date": "2026-03-01", "startedAt": "2026-03-01T08:00:00Z", "notes": "Knee felt sore" }"""));
        await db.WaitForSessionBlockedByAsync(withdrawal.Pid);
        await withdrawal.CommitAsync();
        var response = await pending;

        // Assert: the save saw the committed withdrawal, not the consent it started with.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var context = db.NewContext();
        Assert.False(await context.Workouts.AnyAsync(w => w.UserId == userId));
    }

    [Fact]
    public async Task Withdraw_WaitedBehindSaveHoldingConsent_ClearsTheSavedDetail()
    {
        // Arrange: a save that has already passed the consent check (its FOR SHARE read)
        // and is about to insert its workout, held open.
        await using var host = db.CreateHost(_flagOn);
        var userId = await db.SeedUserAsync();
        await GrantConsentAsync(host, userId);
        using var client = host.ClientFor(userId);
        await using var save = await db.BeginHeldTransactionAsync(userId);
        await save.ExecuteAsync("SELECT 1 FROM users WHERE id = @id FOR SHARE");

        // Act: withdraw while the save is in flight; the save stores its detail and commits.
        var pending = client.DeleteAsync("/account/privacy/optional-details-consent");
        await db.WaitForSessionBlockedByAsync(save.Pid);
        await save.ExecuteAsync("INSERT INTO workouts (user_id, date, started_at, notes) VALUES (@id, DATE '2026-03-01', TIMESTAMPTZ '2026-03-01 08:00Z', 'Knee felt sore')");
        await save.CommitAsync();
        var response = await pending;

        // Assert: the withdrawal cleared the workout the save committed before it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<OptionalDetailsWithdrawalResponse>())?.ClearedWorkouts);
        await using var context = db.NewContext();
        Assert.Null(await context.Workouts.Where(w => w.UserId == userId).Select(w => w.Notes).SingleAsync());
        var consent = await context.Users.Where(u => u.Id == userId).Select(u => u.OptionalDetailsConsentVersion).SingleAsync();
        Assert.Null(consent);
    }

    private static async Task GrantConsentAsync(LifecycleTestHost host, int userId)
    {
        var version = host.Services.GetRequiredService<OptionalDetailsConsentCatalog>().Current.Version;
        using var client = host.ClientFor(userId);
        var response = await client.PutAsJsonAsync("/account/privacy/optional-details-consent", new GrantOptionalDetailsConsentRequest(version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
