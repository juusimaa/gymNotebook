using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// GET /exercises and PATCH /exercises/{id}. Neither exercises nor workouts have a
// creation endpoint yet at this point in milestone 4 (exercises come into being by
// being used, and /workouts doesn't exist until a later PR), so every test here seeds
// the rows it needs directly through AppDbContext — the same way MeTests bumps
// TokenVersion directly to isolate behaviour from the endpoint that would normally
// cause it.
public class ExerciseTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    // Seeds a User directly and mints its token with JwtTokenFactory rather than calling
    // /auth/register: every test in this class shares one host and therefore one
    // in-process rate-limit bucket, and this class makes enough of these calls to sit
    // right at the "auth" policy's default 10-per-60s limit if it went through the real
    // endpoint. Nothing here is testing registration, so there's no reason to pay for it.
    private async Task<(string Token, int UserId)> RegisterAndGetUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new User
        {
            Username = UniqueUsername(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple"),
            TokenVersion = 0,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);
        return (token, user.Id);
    }

    private async Task<Exercise> SeedExerciseAsync(int userId, string name, bool isBodyweight = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exercise = new Exercise
        {
            UserId = userId,
            Name = name,
            NormalizedName = ExerciseNameNormalizer.Normalize(name),
            IsBodyweight = isBodyweight,
        };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync();
        return exercise;
    }

    // A dated session with one block for the exercise and the given sets — what the
    // LastSet tests need, since "last" is decided by the workout's date and start time.
    private async Task<WorkoutExercise> SeedSessionAsync(
        int userId,
        int exerciseId,
        DateOnly date,
        DateTimeOffset startedAt,
        params (int SetNumber, int Reps, decimal? Weight, bool IsWarmup)[] sets)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var workout = new Workout { UserId = userId, Date = date, StartedAt = startedAt };
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();

        var block = new WorkoutExercise { WorkoutId = workout.Id, ExerciseId = exerciseId, Position = 0 };
        db.WorkoutExercises.Add(block);
        await db.SaveChangesAsync();

        foreach (var (setNumber, reps, weight, isWarmup) in sets)
        {
            db.SetEntries.Add(new SetEntry
            {
                WorkoutExerciseId = block.Id,
                SetNumber = setNumber,
                Reps = reps,
                Weight = weight,
                IsWarmup = isWarmup,
            });
        }

        await db.SaveChangesAsync();
        return block;
    }

    private static HttpRequestMessage AuthenticatedGet(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage AuthenticatedPatch(string url, string token, UpdateExerciseRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Search_without_a_token_returns_unauthorized()
    {
        var response = await _client.GetAsync("/exercises");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Search_returns_only_the_callers_exercises()
    {
        var (tokenA, userIdA) = await RegisterAndGetUserAsync();
        var (_, userIdB) = await RegisterAndGetUserAsync();
        await SeedExerciseAsync(userIdA, "Back Squat");
        await SeedExerciseAsync(userIdB, "Bench Press");

        var response = await _client.SendAsync(AuthenticatedGet("/exercises", tokenA));

        var body = await response.Content.ReadFromJsonAsync<List<ExerciseResponse>>();
        Assert.Single(body!);
        Assert.Equal("Back Squat", body![0].Name);
    }

    [Fact]
    public async Task Search_with_term_filters_to_matching_exercises()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        await SeedExerciseAsync(userId, "Back Squat");
        await SeedExerciseAsync(userId, "Bench Press");

        var response = await _client.SendAsync(AuthenticatedGet("/exercises?search=squat", token));

        var body = await response.Content.ReadFromJsonAsync<List<ExerciseResponse>>();
        Assert.Single(body!);
        Assert.Equal("Back Squat", body![0].Name);
    }

    [Fact]
    public async Task Search_returns_the_last_working_set_of_the_latest_session_and_null_when_never_logged()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        await SeedExerciseAsync(userId, "Bench Press");

        // The older session has the heavier set; the hint must still come from the newer
        // one, and from its last working set (set 3), not its heaviest or its warm-up.
        await SeedSessionAsync(userId, backSquat.Id, new DateOnly(2026, 1, 8), new DateTimeOffset(2026, 1, 8, 7, 0, 0, TimeSpan.Zero),
            (SetNumber: 1, Reps: 3, Weight: 100m, IsWarmup: false));
        await SeedSessionAsync(userId, backSquat.Id, new DateOnly(2026, 1, 10), new DateTimeOffset(2026, 1, 10, 7, 0, 0, TimeSpan.Zero),
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true),
            (SetNumber: 2, Reps: 5, Weight: 95m, IsWarmup: false),
            (SetNumber: 3, Reps: 5, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(AuthenticatedGet("/exercises", token));
        var body = await response.Content.ReadFromJsonAsync<List<ExerciseResponse>>();

        var squat = Assert.Single(body!, e => e.Name == "Back Squat");
        Assert.Equal(new LastSetResponse(90m, 5), squat.LastSet);

        var bench = Assert.Single(body!, e => e.Name == "Bench Press");
        Assert.Null(bench.LastSet);
    }

    [Fact]
    public async Task Search_falls_back_to_a_warm_up_when_the_latest_session_logged_nothing_else()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");

        await SeedSessionAsync(userId, backSquat.Id, new DateOnly(2026, 1, 8), new DateTimeOffset(2026, 1, 8, 7, 0, 0, TimeSpan.Zero),
            (SetNumber: 1, Reps: 5, Weight: 90m, IsWarmup: false));
        // Two sessions on the same date: the later start is "latest", and it only has a
        // warm-up — which is still what "last time" was.
        await SeedSessionAsync(userId, backSquat.Id, new DateOnly(2026, 1, 8), new DateTimeOffset(2026, 1, 8, 18, 0, 0, TimeSpan.Zero),
            (SetNumber: 1, Reps: 8, Weight: 40m, IsWarmup: true));

        var response = await _client.SendAsync(AuthenticatedGet("/exercises", token));
        var body = await response.Content.ReadFromJsonAsync<List<ExerciseResponse>>();

        Assert.Equal(new LastSetResponse(40m, 8), Assert.Single(body!).LastSet);
    }

    [Fact]
    public async Task Update_without_a_token_returns_unauthorized()
    {
        var response = await _client.PatchAsync("/exercises/1", JsonContent.Create(new UpdateExerciseRequest("Whatever", null)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_for_another_users_exercise_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userIdA, "Back Squat");

        var response = await _client.SendAsync(
            AuthenticatedPatch($"/exercises/{exercise.Id}", tokenB, new UpdateExerciseRequest("Hijacked", null)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_with_unknown_id_returns_not_found()
    {
        var (token, _) = await RegisterAndGetUserAsync();

        var response = await _client.SendAsync(
            AuthenticatedPatch("/exercises/999999", token, new UpdateExerciseRequest("Whatever", null)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_name_renames_the_exercise()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userId, "Bnech Press");

        var response = await _client.SendAsync(
            AuthenticatedPatch($"/exercises/{exercise.Id}", token, new UpdateExerciseRequest("Bench Press", null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExerciseResponse>();
        Assert.Equal("Bench Press", body!.Name);
    }

    [Fact]
    public async Task Update_is_bodyweight_toggles_the_flag_without_touching_the_name()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userId, "Pull Up", isBodyweight: false);

        var response = await _client.SendAsync(
            AuthenticatedPatch($"/exercises/{exercise.Id}", token, new UpdateExerciseRequest(null, true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExerciseResponse>();
        Assert.Equal("Pull Up", body!.Name);
        Assert.True(body.IsBodyweight);
    }

    [Fact]
    public async Task Update_name_that_only_differs_by_casing_renames_without_a_merge()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userId, "back squat");

        var response = await _client.SendAsync(
            AuthenticatedPatch($"/exercises/{exercise.Id}", token, new UpdateExerciseRequest("Back Squat", null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExerciseResponse>();
        Assert.Equal("Back Squat", body!.Name);
    }

    [Fact]
    public async Task Renaming_onto_an_existing_exercise_merges_them_and_repoints_its_blocks()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var typo = await SeedExerciseAsync(userId, "Bnech Press");
        var existing = await SeedExerciseAsync(userId, "Bench Press");
        var block = await SeedSessionAsync(userId, existing.Id, new DateOnly(2026, 1, 8), new DateTimeOffset(2026, 1, 8, 7, 0, 0, TimeSpan.Zero),
            (SetNumber: 1, Reps: 5, Weight: 65m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedPatch($"/exercises/{typo.Id}", token, new UpdateExerciseRequest("Bench Press", null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExerciseResponse>();

        // The exercise being PATCHed survives, under its own id, with the new name — and
        // the response already reflects the sets the merge re-pointed onto it.
        Assert.Equal(typo.Id, body!.Id);
        Assert.Equal("Bench Press", body.Name);
        Assert.Equal(new LastSetResponse(65m, 5), body.LastSet);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The one it collided with is gone...
        Assert.False(await db.Exercises.AnyAsync(e => e.Id == existing.Id));

        // ...and its block now points at the survivor instead of the deleted row.
        var reloadedBlock = await db.WorkoutExercises.SingleAsync(we => we.Id == block.Id);
        Assert.Equal(typo.Id, reloadedBlock.ExerciseId);
    }
}
