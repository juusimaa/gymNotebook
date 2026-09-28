using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/001 user story 6 (tasks.md T086): with the feature on, the existing workout routes
// refuse to store an optional detail (title, location, notes, bodyweight) for an account
// without consent (FR-032, contracts/api.md → Optional-details enforcement). Server-side:
// hiding the inputs in the UI is not enough.
public class OptionalDetailsEnforcementTests(PrivacyEnabledGymNotebookFactory factory) : IClassFixture<PrivacyEnabledGymNotebookFactory>
{
    // One optional detail per case, as raw JSON, so each field is shown to be enforced on
    // its own. The rest of the body is a valid workout, so a 403 can only be the detail.
    public static TheoryData<string> EachDetail =>
    [
        "\"title\": \"Leg day\"",
        "\"location\": \"Home gym\"",
        "\"notes\": \"Knee felt sore\"",
        "\"bodyweightKg\": 82.4",
    ];

    private string CurrentVersion =>
        factory.Services.GetRequiredService<OptionalDetailsConsentCatalog>().Current.Version;

    [Theory]
    [MemberData(nameof(EachDetail))]
    public async Task CreateWorkout_NoConsentWithDetail_Returns403AndStoresNothing(string detail)
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act
        var response = await client.PostAsync("/workouts", Json($$"""{ "date": "2026-03-01", "startedAt": "2026-03-01T08:00:00Z", {{detail}} }"""));

        // Assert: rejected as a whole — not even the date and start time were stored.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"code\":\"optional_details_consent_required\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId));
    }

    [Theory]
    [MemberData(nameof(EachDetail))]
    public async Task UpdateWorkout_NoConsentWithDetail_Returns403AndChangesNothing(string detail)
    {
        // Arrange: the request also moves the date, which must not be applied either.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        var workoutId = await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: false);

        // Act
        var response = await client.PatchAsync($"/workouts/{workoutId}", Json($$"""{ "date": "2026-04-02", {{detail}} }"""));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"code\":\"optional_details_consent_required\"", await response.Content.ReadAsStringAsync());
        var stored = (await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId)).Single();
        Assert.Equal(new DateOnly(2026, 3, 1), stored.Date);
        Assert.Null(stored.Title);
        Assert.Null(stored.Location);
        Assert.Null(stored.Notes);
        Assert.Null(stored.BodyweightKg);
    }

    [Fact]
    public async Task CreateWorkout_NoConsentWithNullEmptyOrOmittedDetails_Returns201()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act: title empty, location null, notes empty, bodyweight omitted.
        var response = await client.PostAsync("/workouts", Json("""{ "date": "2026-03-01", "startedAt": "2026-03-01T08:00:00Z", "title": "", "location": null, "notes": "" }"""));

        // Assert: accepted, and without consent an empty detail is stored as null, so
        // "the account holds a detail" means "a detail is not null" everywhere.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = (await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId)).Single();
        Assert.Null(stored.Title);
        Assert.Null(stored.Location);
        Assert.Null(stored.Notes);
        Assert.Null(stored.BodyweightKg);
    }

    [Fact]
    public async Task UpdateWorkout_NoConsentClearingDetails_Returns200()
    {
        // Arrange: a transition account, clearing details it held from before the feature.
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        var workoutId = await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: true);

        // Act
        var response = await client.PatchAsync($"/workouts/{workoutId}", Json("""{ "title": null, "notes": "" }"""));

        // Assert: both cleared; the details that weren't in the request are left alone.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId)).Single();
        Assert.Null(stored.Title);
        Assert.Null(stored.Notes);
        Assert.Equal(OptionalDetailsTestData.Location, stored.Location);
        Assert.Equal(OptionalDetailsTestData.BodyweightKg, stored.BodyweightKg);
    }

    [Fact]
    public async Task PutWorkoutExercises_NoConsent_ExerciseNamesStored()
    {
        // Arrange: exercise names are outside the consent (FR-029).
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);
        var workoutId = await OptionalDetailsTestData.SeedWorkoutAsync(factory, userId, withDetails: false);

        // Act
        var response = await client.PutAsync($"/workouts/{workoutId}/exercises",
            Json("""{ "exercises": [ { "exerciseName": "Bench press", "sets": [ { "weight": 60, "reps": 8, "isWarmup": false } ] } ] }"""));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateAndUpdateWorkout_WithConsent_StoreAllFourDetails()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory, consentVersion: CurrentVersion);

        // Act
        var created = await client.PostAsync("/workouts",
            Json("""{ "date": "2026-03-01", "startedAt": "2026-03-01T08:00:00Z", "title": "Leg day", "location": "Home gym", "notes": "Knee felt sore", "bodyweightKg": 82.4 }"""));
        var workout = await created.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        var updated = await client.PatchAsync($"/workouts/{workout!.Id}", Json("""{ "notes": "Knee fine after warming up" }"""));

        // Assert
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stored = (await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId)).Single();
        Assert.Equal("Leg day", stored.Title);
        Assert.Equal("Home gym", stored.Location);
        Assert.Equal("Knee fine after warming up", stored.Notes);
        Assert.Equal(82.4m, stored.BodyweightKg);
    }

    internal static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");
}

// The flag off: production's state until T084. Workout writes behave exactly as before
// the feature, details included, with no consent anywhere (the owner-accepted interim).
public class OptionalDetailsFlagOffTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    [Fact]
    public async Task CreateAndUpdateWorkout_FlagOffNoConsent_StoreDetails()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act
        var created = await client.PostAsync("/workouts",
            OptionalDetailsEnforcementTests.Json("""{ "date": "2026-03-01", "startedAt": "2026-03-01T08:00:00Z", "title": "Leg day", "bodyweightKg": 82.4 }"""));
        var workout = await created.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        var updated = await client.PatchAsync($"/workouts/{workout!.Id}", OptionalDetailsEnforcementTests.Json("""{ "notes": "Knee felt sore" }"""));

        // Assert
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stored = (await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId)).Single();
        Assert.Equal("Leg day", stored.Title);
        Assert.Equal("Knee felt sore", stored.Notes);
        Assert.Equal(82.4m, stored.BodyweightKg);
    }
}
