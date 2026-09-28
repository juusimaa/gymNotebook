using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/001 user story 6 (tasks.md T085): the consent statement, granting and withdrawing
// consent for the optional workout details (title, location, notes, bodyweight), and the
// optionalDetails part of the account's privacy state. The flag-off 404s for these routes
// are in PrivacyFlagDisabledTests (PrivacyNoticeTests.cs), which runs over every flag value
// that must leave the feature off.
public class OptionalDetailsConsentTests(PrivacyEnabledGymNotebookFactory factory) : IClassFixture<PrivacyEnabledGymNotebookFactory>
{
    private const string ConsentPath = "/account/privacy/optional-details-consent";

    private string CurrentVersion =>
        factory.Services.GetRequiredService<OptionalDetailsConsentCatalog>().Current.Version;

    [Fact]
    public async Task GetStatement_Anonymous_ReturnsCurrentStatementWithoutSuccessor()
    {
        // Arrange: no Authorization header at all.
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/privacy/optional-details-statement");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(CurrentVersion, body.RootElement.GetProperty("version").GetString());
        Assert.NotEqual(0, body.RootElement.GetProperty("sections").GetArrayLength());
        // Same shape as the notice document, minus the successor mechanism (FR-034).
        Assert.False(body.RootElement.TryGetProperty("announcedSuccessor", out _));
    }

    [Fact]
    public async Task ConsentRoutes_WithoutToken_Return401()
    {
        // Arrange
        var client = factory.CreateClient();

        // Act
        var grant = await client.PutAsJsonAsync(ConsentPath, new GrantOptionalDetailsConsentRequest(CurrentVersion));
        var withdraw = await client.DeleteAsync(ConsentPath);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, grant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withdraw.StatusCode);
    }

    [Fact]
    public async Task GrantConsent_CurrentVersion_RecordsItAndReportsConsent()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync(ConsentPath, new GrantOptionalDetailsConsentRequest(CurrentVersion));

        // Assert: the response, the stored pair and the account state all agree.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var consent = await response.Content.ReadFromJsonAsync<OptionalDetailsConsentResponse>();
        Assert.NotNull(consent);
        Assert.Equal(CurrentVersion, consent.StatementVersion);

        var stored = await OptionalDetailsTestData.ReadConsentAsync(factory, userId);
        Assert.Equal(CurrentVersion, stored.Version);
        Assert.Equal(consent.ConsentedAt, stored.ConsentedAt);

        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");
        Assert.NotNull(state);
        Assert.Equal(CurrentVersion, state.OptionalDetails.CurrentStatementVersion);
        Assert.Equal(consent, state.OptionalDetails.Consent);
        Assert.False(state.OptionalDetails.TransitionPending);
    }

    [Fact]
    public async Task GrantConsent_OtherVersion_Returns409AndRecordsNothing()
    {
        // Arrange: a tab still showing a statement the server no longer serves.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync(ConsentPath, new GrantOptionalDetailsConsentRequest("not-the-current-version"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"code\":\"consent_statement_changed\"", await response.Content.ReadAsStringAsync());
        Assert.Null((await OptionalDetailsTestData.ReadConsentAsync(factory, userId)).Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GrantConsent_MissingVersion_Returns400(string? version)
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act
        var response = await client.PutAsJsonAsync(ConsentPath, new GrantOptionalDetailsConsentRequest(version));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"code\":\"invalid_request\"", await response.Content.ReadAsStringAsync());
        Assert.Null((await OptionalDetailsTestData.ReadConsentAsync(factory, userId)).Version);
    }

    [Fact]
    public async Task GrantConsent_SameVersionTwice_KeepsOriginalTimestamp()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        var first = await client.PutAsJsonAsync(ConsentPath, new GrantOptionalDetailsConsentRequest(CurrentVersion));
        var firstConsent = await first.Content.ReadFromJsonAsync<OptionalDetailsConsentResponse>();

        // Act: a retry, e.g. after a lost response.
        var second = await client.PutAsJsonAsync(ConsentPath, new GrantOptionalDetailsConsentRequest(CurrentVersion));

        // Assert
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondConsent = await second.Content.ReadFromJsonAsync<OptionalDetailsConsentResponse>();
        Assert.Equal(firstConsent, secondConsent);
        Assert.Equal(firstConsent!.ConsentedAt, (await OptionalDetailsTestData.ReadConsentAsync(factory, userId)).ConsentedAt);
    }

    [Fact]
    public async Task GetAccountPrivacy_NoDetailsNoConsent_NoConsentAndNoTransition()
    {
        // Arrange: a fresh account with a workout that holds no optional detail — the
        // account the UI must never prompt automatically (FR-031).
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: false);

        // Act
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");

        // Assert
        Assert.NotNull(state);
        Assert.Null(state.OptionalDetails.Consent);
        Assert.False(state.OptionalDetails.TransitionPending);
    }

    [Fact]
    public async Task GetAccountPrivacy_DetailsWithoutConsent_TransitionPending()
    {
        // Arrange: an existing account from before the feature, holding one detail only
        // (bodyweight), so every one of the four fields is shown to count.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: false, bodyweightKg: 80.5m);

        // Act
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");

        // Assert
        Assert.NotNull(state);
        Assert.Null(state.OptionalDetails.Consent);
        Assert.True(state.OptionalDetails.TransitionPending);
    }

    [Fact]
    public async Task GetAccountPrivacy_DetailsWithConsent_NoTransition()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory, consentVersion: CurrentVersion);
        await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: true);

        // Act
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");

        // Assert
        Assert.NotNull(state);
        Assert.NotNull(state.OptionalDetails.Consent);
        Assert.False(state.OptionalDetails.TransitionPending);
    }

    [Fact]
    public async Task WithdrawConsent_WithDetails_ClearsPairAndCallersDetailsOnly()
    {
        // Arrange: the caller has consent, two workouts with details and one without; a
        // canary account has consent and details too.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory, consentVersion: CurrentVersion);
        var withDetails = new[]
        {
            await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: true),
            await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: true),
        };
        await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: false);
        var (_, canaryId) = await OptionalDetailsTestData.CreateAccountAsync(factory, consentVersion: CurrentVersion);
        var canaryWorkout = await OptionalDetailsTestData.SeedWorkoutAsync(factory, canaryId, withDetails: true);
        var before = await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId);

        // Act
        var response = await client.DeleteAsync(ConsentPath);

        // Assert: the count covers the workouts that held a detail.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var withdrawal = await response.Content.ReadFromJsonAsync<OptionalDetailsWithdrawalResponse>();
        Assert.Equal(withDetails.Length, withdrawal?.ClearedWorkouts);

        // The pair is gone, and so is every detail on every one of the caller's workouts.
        var consent = await OptionalDetailsTestData.ReadConsentAsync(factory, userId);
        Assert.Null(consent.Version);
        Assert.Null(consent.ConsentedAt);
        var after = await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId);
        Assert.All(after, w =>
        {
            Assert.Null(w.Title);
            Assert.Null(w.Location);
            Assert.Null(w.Notes);
            Assert.Null(w.BodyweightKg);
        });

        // Every other field and row stays exactly as it was.
        Assert.Equal(
            before.Select(w => (w.Id, w.Date, w.StartedAt, w.EndedAt, w.CreatedAt)),
            after.Select(w => (w.Id, w.Date, w.StartedAt, w.EndedAt, w.CreatedAt)));
        Assert.Equal(
            await OptionalDetailsTestData.CountSetsAsync(factory, before.Select(w => w.Id)),
            before.Count);

        // The canary account is untouched.
        Assert.Equal(CurrentVersion, (await OptionalDetailsTestData.ReadConsentAsync(factory, canaryId)).Version);
        var canary = (await OptionalDetailsTestData.ReadWorkoutsAsync(factory, canaryId)).Single(w => w.Id == canaryWorkout);
        Assert.Equal(OptionalDetailsTestData.Notes, canary.Notes);
    }

    [Fact]
    public async Task WithdrawConsent_Repeated_ReturnsZeroClearedWorkouts()
    {
        // Arrange: a first withdrawal whose response the client may have lost.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory, consentVersion: CurrentVersion);
        await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: true);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(ConsentPath)).StatusCode);

        // Act
        var retry = await client.DeleteAsync(ConsentPath);

        // Assert: safe to retry, and never claims clearing that didn't happen this time.
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var withdrawal = await retry.Content.ReadFromJsonAsync<OptionalDetailsWithdrawalResponse>();
        Assert.Equal(0, withdrawal?.ClearedWorkouts);
    }

    [Fact]
    public async Task WithdrawConsent_TransitionAccountDoesNotAllow_ClearsDetails()
    {
        // Arrange: "Don't allow" in the transition question uses the same route: no pair to
        // clear, but details from before the feature.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: true);

        // Act
        var response = await client.DeleteAsync(ConsentPath);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<OptionalDetailsWithdrawalResponse>())?.ClearedWorkouts);
        var state = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");
        Assert.False(state!.OptionalDetails.TransitionPending);
    }
}

// Seeding and reading helpers shared by the user story 6 test classes. Accounts are seeded
// directly, not registered, to stay clear of the per-IP rate limiter the whole class shares.
internal static class OptionalDetailsTestData
{
    public const string Title = "Leg day";
    public const string Location = "Home gym";
    public const string Notes = "Knee felt sore after the second set";
    public const decimal BodyweightKg = 82.4m;

    public static async Task<(HttpClient Client, int UserId)> CreateAccountAsync(GymNotebookFactory factory, string? consentVersion = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = $"user-{Guid.NewGuid():N}",
            PasswordHash = "not-a-real-hash",
            PrivacyAccountId = Guid.NewGuid(),
            OptionalDetailsConsentVersion = consentVersion,
            OptionalDetailsConsentedAt = consentVersion is null ? null : DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TwoHostGymNotebookFixture.Token(user.Id));
        return (client, user.Id);
    }

    // One workout with one block and one set. `withDetails` fills all four optional fields;
    // `bodyweightKg` alone sets just that one.
    public static async Task<int> SeedWorkoutAsync(GymNotebookFactory factory, int userId, bool withDetails, decimal? bodyweightKg = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exercise = new Exercise { UserId = userId, Name = $"Squat {Guid.NewGuid():N}", NormalizedName = $"squat {Guid.NewGuid():N}" };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync();

        var workout = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 3, 1),
            StartedAt = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
            Title = withDetails ? Title : null,
            Location = withDetails ? Location : null,
            Notes = withDetails ? Notes : null,
            BodyweightKg = withDetails ? BodyweightKg : bodyweightKg,
        };
        var block = new WorkoutExercise { ExerciseId = exercise.Id, Position = 0 };
        block.SetEntries.Add(new SetEntry { SetNumber = 1, Reps = 5, Weight = 100 });
        workout.WorkoutExercises.Add(block);
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();
        return workout.Id;
    }

    public static async Task<(string? Version, DateTimeOffset? ConsentedAt)> ReadConsentAsync(GymNotebookFactory factory, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.OptionalDetailsConsentVersion, u.OptionalDetailsConsentedAt })
            .SingleAsync();
        return (row.OptionalDetailsConsentVersion, row.OptionalDetailsConsentedAt);
    }

    public static async Task<List<Workout>> ReadWorkoutsAsync(GymNotebookFactory factory, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Workouts.AsNoTracking().Where(w => w.UserId == userId).OrderBy(w => w.Id).ToListAsync();
    }

    public static async Task<int> CountSetsAsync(GymNotebookFactory factory, IEnumerable<int> workoutIds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = workoutIds.ToList();
        return await db.SetEntries.CountAsync(s => db.WorkoutExercises.Any(we => we.Id == s.WorkoutExerciseId && ids.Contains(we.WorkoutId)));
    }
}
