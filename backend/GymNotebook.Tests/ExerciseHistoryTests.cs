using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

public class ExerciseHistoryTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(string Token, int UserId)> RegisterAndGetUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = $"user-{Guid.NewGuid():N}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple"),
            TokenVersion = 0,
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (
            JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes),
            user.Id);
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

    private async Task<Workout> SeedSessionAsync(
        int userId,
        int exerciseId,
        DateOnly date,
        DateTimeOffset startedAt,
        params (int Reps, decimal? Weight, bool IsWarmup)[] sets)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var workout = new Workout { UserId = userId, Date = date, StartedAt = startedAt };
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();

        var block = new WorkoutExercise { WorkoutId = workout.Id, ExerciseId = exerciseId, Position = 0 };
        db.WorkoutExercises.Add(block);
        await db.SaveChangesAsync();

        for (var index = 0; index < sets.Length; index++)
        {
            var set = sets[index];
            db.SetEntries.Add(new SetEntry
            {
                WorkoutExerciseId = block.Id,
                SetNumber = index + 1,
                Reps = set.Reps,
                Weight = set.Weight,
                IsWarmup = set.IsWarmup,
            });
        }

        await db.SaveChangesAsync();
        return workout;
    }

    private static HttpRequestMessage AuthenticatedGet(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task History_without_a_token_returns_unauthorized()
    {
        var response = await _client.GetAsync("/exercises/1/history");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task History_for_another_users_exercise_returns_not_found()
    {
        var (_, ownerId) = await RegisterAndGetUserAsync();
        var (callerToken, _) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(ownerId, "Back Squat");

        var response = await _client.SendAsync(
            AuthenticatedGet($"/exercises/{exercise.Id}/history", callerToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task History_returns_best_loaded_e1rm_per_workout_and_keeps_same_day_sessions_separate()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userId, "Back Squat");
        var morning = await SeedSessionAsync(
            userId,
            exercise.Id,
            new DateOnly(2026, 9, 8),
            new DateTimeOffset(2026, 9, 8, 4, 15, 0, TimeSpan.Zero),
            (Reps: 5, Weight: 120m, IsWarmup: true),
            (Reps: 3, Weight: 90m, IsWarmup: false),
            (Reps: 5, Weight: 80m, IsWarmup: false),
            (Reps: 10, Weight: null, IsWarmup: false));
        var evening = await SeedSessionAsync(
            userId,
            exercise.Id,
            new DateOnly(2026, 9, 8),
            new DateTimeOffset(2026, 9, 8, 15, 20, 0, TimeSpan.Zero),
            (Reps: 1, Weight: 100m, IsWarmup: false),
            (Reps: 3, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedGet($"/exercises/{exercise.Id}/history", token));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ExerciseHistoryResponse>();
        Assert.False(body!.IsBodyweight);
        Assert.Equal(new[] { morning.Id, evening.Id }, body.Points.Select(point => point.WorkoutId));
        Assert.Equal(99m, body.Points[0].Value);
        Assert.Equal(90m, body.Points[0].Weight);
        Assert.Equal(3, body.Points[0].Reps);
        // A measured 100 kg single beats the 99 kg estimate and is not inflated.
        Assert.Equal(100m, body.Points[1].Value);
        Assert.Equal(1, body.Points[1].Reps);
    }

    [Fact]
    public async Task History_for_bodyweight_exercise_uses_reps_and_excludes_added_weight_sets()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userId, "Pull-up", isBodyweight: true);
        await SeedSessionAsync(
            userId,
            exercise.Id,
            new DateOnly(2026, 9, 8),
            new DateTimeOffset(2026, 9, 8, 15, 20, 0, TimeSpan.Zero),
            (Reps: 12, Weight: null, IsWarmup: true),
            (Reps: 8, Weight: null, IsWarmup: false),
            (Reps: 10, Weight: null, IsWarmup: false),
            (Reps: 5, Weight: 10m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedGet($"/exercises/{exercise.Id}/history", token));

        response.EnsureSuccessStatusCode();
        var point = Assert.Single((await response.Content.ReadFromJsonAsync<ExerciseHistoryResponse>())!.Points);
        Assert.Equal(10m, point.Value);
        Assert.Null(point.Weight);
        Assert.Equal(10, point.Reps);
    }

    [Fact]
    public async Task History_date_range_is_inclusive_and_rejects_reversed_range()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var exercise = await SeedExerciseAsync(userId, "Bench Press");
        await SeedSessionAsync(
            userId,
            exercise.Id,
            new DateOnly(2026, 9, 1),
            new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero),
            (Reps: 5, Weight: 60m, IsWarmup: false));
        await SeedSessionAsync(
            userId,
            exercise.Id,
            new DateOnly(2026, 9, 8),
            new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero),
            (Reps: 5, Weight: 65m, IsWarmup: false));

        var filtered = await _client.SendAsync(
            AuthenticatedGet($"/exercises/{exercise.Id}/history?from=2026-09-08&to=2026-09-08", token));
        var reversed = await _client.SendAsync(
            AuthenticatedGet($"/exercises/{exercise.Id}/history?from=2026-09-09&to=2026-09-08", token));

        filtered.EnsureSuccessStatusCode();
        var point = Assert.Single((await filtered.Content.ReadFromJsonAsync<ExerciseHistoryResponse>())!.Points);
        Assert.Equal(new DateOnly(2026, 9, 8), point.Date);
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
    }
}
