using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// POST/GET/GET-list/PATCH/DELETE /workouts. Exercise blocks and sets have no creation
// endpoint yet at this point in milestone 4 (that's PR (d)'s bulk write), so the
// ordering test below seeds them directly through AppDbContext — the same pattern
// ExerciseTests uses to seed the WorkoutExercise row its merge test needs.
public class WorkoutTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    // Seeds a User directly and mints its token with JwtTokenFactory rather than calling
    // /auth/register: every test in this class shares one host and therefore one
    // in-process rate-limit bucket, and this class alone makes enough of these calls to
    // trip the "auth" policy's default 10-per-60s limit if it went through the real
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
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);
        return (token, user.Id);
    }

    private async Task<Workout> SeedWorkoutAsync(
        int userId,
        DateOnly date,
        DateTimeOffset startedAt,
        string? title = null,
        string? location = null,
        decimal? bodyweightKg = null,
        string? notes = null,
        DateTimeOffset? endedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workout = new Workout
        {
            UserId = userId,
            Date = date,
            StartedAt = startedAt,
            Title = title,
            Location = location,
            BodyweightKg = bodyweightKg,
            Notes = notes,
            EndedAt = endedAt,
        };
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();
        return workout;
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

    // Seeds one exercise block at the given position, with the given (setNumber, reps,
    // weight, isWarmup) sets attached. The ordering test below inserts these out of
    // order on purpose, so a passing assertion proves the endpoint actually sorts by
    // Position/SetNumber rather than returning whatever order Postgres stored them in.
    private async Task SeedBlockWithSetsAsync(
        int workoutId,
        int exerciseId,
        int position,
        params (int SetNumber, int Reps, decimal? Weight, bool IsWarmup)[] sets)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var block = new WorkoutExercise
        {
            WorkoutId = workoutId,
            ExerciseId = exerciseId,
            Position = position,
        };
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
    }

    // One helper for every authenticated verb, since GET/POST/PATCH/DELETE all need the
    // same bearer header attached and only sometimes need a body.
    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Create_without_a_token_returns_unauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/workouts",
            new CreateWorkoutRequest(new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow, null, null, null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_returns_created_with_location_and_the_new_workout()
    {
        var (token, _) = await RegisterAndGetUserAsync();
        var request = new CreateWorkoutRequest(
            new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), "Leg day", 78.4m, "Gym", "notes");

        var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/workouts", token, request));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal($"/workouts/{body!.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Leg day", body.Title);
        Assert.Empty(body.Exercises);
    }

    [Fact]
    public async Task Create_with_non_utc_started_at_normalizes_the_instant_and_preserves_the_local_date()
    {
        var (token, _) = await RegisterAndGetUserAsync();
        var localDate = new DateOnly(2026, 9, 15);
        // 00.30 in Helsinki falls on the previous UTC calendar day. The separate
        // workout date must remain the date the lifter chose for the notebook page.
        var localStartedAt = new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.FromHours(3));
        var expectedUtc = new DateTimeOffset(2026, 9, 14, 21, 30, 0, TimeSpan.Zero);
        var request = new CreateWorkoutRequest(localDate, localStartedAt, null, null, null, null);

        var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/workouts", token, request));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(localDate, body!.Date);
        Assert.Equal(expectedUtc, body.StartedAt);
        Assert.Equal(TimeSpan.Zero, body.StartedAt.Offset);
    }

    [Fact]
    public async Task List_without_a_token_returns_unauthorized()
    {
        var response = await _client.GetAsync("/workouts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_returns_only_the_callers_workouts()
    {
        var (tokenA, userIdA) = await RegisterAndGetUserAsync();
        var (_, userIdB) = await RegisterAndGetUserAsync();
        await SeedWorkoutAsync(userIdA, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        await SeedWorkoutAsync(userIdB, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));

        var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/workouts", tokenA));

        var body = await response.Content.ReadFromJsonAsync<List<WorkoutSummaryResponse>>();
        Assert.Single(body!);
    }

    [Fact]
    public async Task List_orders_newest_date_first_and_breaks_ties_by_started_at()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var morning = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), "Morning");
        var evening = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 18, 0, 0, TimeSpan.Zero), "Evening");
        var nextDay = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 2), new DateTimeOffset(2026, 1, 2, 7, 0, 0, TimeSpan.Zero), "Next day");

        var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/workouts", token));
        var body = await response.Content.ReadFromJsonAsync<List<WorkoutSummaryResponse>>();

        Assert.Equal(new[] { nextDay.Id, evening.Id, morning.Id }, body!.Select(w => w.Id));
    }

    [Fact]
    public async Task List_summaries_carry_end_time_counts_and_exercise_names_in_position_order()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), "Leg day");
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var calfRaise = await SeedExerciseAsync(userId, "Standing Calf Raise");
        // Seeded out of position order, as in the GET test, so the names have to be
        // sorted by position rather than by insertion.
        await SeedBlockWithSetsAsync(workout.Id, calfRaise.Id, position: 1,
            (SetNumber: 1, Reps: 12, Weight: 60m, IsWarmup: false));
        await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true),
            (SetNumber: 2, Reps: 5, Weight: 80m, IsWarmup: false));
        // An empty page alongside it: zero blocks must come back as zeros and an empty
        // list, not as a missing row or a null.
        var empty = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 2), new DateTimeOffset(2026, 1, 2, 7, 0, 0, TimeSpan.Zero));
        var endedAt = new DateTimeOffset(2026, 1, 1, 9, 25, 0, TimeSpan.Zero);
        await _client.SendAsync(AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
            new { endedAt }));

        var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/workouts", token));
        var body = await response.Content.ReadFromJsonAsync<List<WorkoutSummaryResponse>>();

        var legDay = Assert.Single(body!, w => w.Id == workout.Id);
        Assert.Equal("Leg day", legDay.Title);
        Assert.Equal(endedAt, legDay.EndedAt);
        Assert.Equal(2, legDay.ExerciseCount);
        Assert.Equal(3, legDay.SetCount);
        Assert.Equal(new[] { "Back Squat", "Standing Calf Raise" }, legDay.ExerciseNames);

        var emptyPage = Assert.Single(body!, w => w.Id == empty.Id);
        Assert.Null(emptyPage.EndedAt);
        Assert.Equal(0, emptyPage.ExerciseCount);
        Assert.Equal(0, emptyPage.SetCount);
        Assert.Empty(emptyPage.ExerciseNames);
    }

    [Fact]
    public async Task List_with_before_cursor_returns_the_next_older_page()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var morning = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var evening = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 18, 0, 0, TimeSpan.Zero));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/workouts?limit=1&before={evening.Id}", token));

        var body = await response.Content.ReadFromJsonAsync<List<WorkoutSummaryResponse>>();
        Assert.Equal(new[] { morning.Id }, body!.Select(w => w.Id));
    }

    [Fact]
    public async Task List_with_an_unknown_before_cursor_returns_bad_request()
    {
        var (token, _) = await RegisterAndGetUserAsync();

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/workouts?before=999999", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_without_a_token_returns_unauthorized()
    {
        var response = await _client.GetAsync("/workouts/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_for_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/workouts/{workout.Id}", tokenB));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_with_unknown_id_returns_not_found()
    {
        var (token, _) = await RegisterAndGetUserAsync();

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/workouts/999999", token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_returns_exercises_in_position_order_with_sets_in_set_number_order()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var benchPress = await SeedExerciseAsync(userId, "Bench Press");
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");

        // Back Squat's block is seeded first but placed at position 1; Bench Press's
        // block is seeded second but placed at position 0 — insertion order and
        // position order deliberately disagree.
        await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 1,
            (SetNumber: 2, Reps: 3, Weight: 90m, IsWarmup: false),
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true));
        await SeedBlockWithSetsAsync(workout.Id, benchPress.Id, position: 0,
            (SetNumber: 1, Reps: 8, Weight: 40m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/workouts/{workout.Id}", token));

        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();

        Assert.Equal(new[] { "Bench Press", "Back Squat" }, body!.Exercises.Select(e => e.ExerciseName));
        Assert.Equal(new[] { 1, 2 }, body.Exercises[1].Sets.Select(s => s.SetNumber));
    }

    [Fact]
    public async Task Get_marks_each_block_with_its_exercises_bodyweight_flag()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var pullUp = await SeedExerciseAsync(userId, "Pull-up", isBodyweight: true);
        var barbellRow = await SeedExerciseAsync(userId, "Barbell Row");
        await SeedBlockWithSetsAsync(workout.Id, pullUp.Id, position: 0, (SetNumber: 1, Reps: 8, Weight: null, IsWarmup: false));
        await SeedBlockWithSetsAsync(workout.Id, barbellRow.Id, position: 1, (SetNumber: 1, Reps: 8, Weight: 50m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/workouts/{workout.Id}", token));

        // The session page branches on this to show "best N reps" instead of an e1RM
        // (docs/ui rule 3), so it has to ride on the block rather than need a second
        // lookup against /exercises.
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(new[] { true, false }, body!.Exercises.Select(e => e.IsBodyweight));
    }

    [Fact]
    public async Task Update_without_a_token_returns_unauthorized()
    {
        var response = await _client.PatchAsync(
            "/workouts/1",
            JsonContent.Create(new { title = "Whatever" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_for_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", tokenB,
                new { title = "Hijacked" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_only_changes_the_fields_that_were_provided()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow, title: "Original", location: "Gym A");

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
                new { title = "Updated" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal("Updated", body!.Title);
        Assert.Equal("Gym A", body.Location);
    }

    [Fact]
    public async Task Update_sets_ended_at_for_finishing_a_session()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var endedAt = new DateTimeOffset(2026, 1, 1, 9, 15, 0, TimeSpan.Zero);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
                new { endedAt }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(endedAt, body!.EndedAt);
    }

    [Fact]
    public async Task Update_with_non_utc_timestamps_normalizes_both_instants()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var localDate = new DateOnly(2026, 9, 15);
        var workout = await SeedWorkoutAsync(
            userId,
            localDate,
            new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));
        var localStartedAt = new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.FromHours(3));
        var localEndedAt = new DateTimeOffset(2026, 9, 15, 1, 45, 0, TimeSpan.FromHours(3));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
                new { startedAt = localStartedAt, endedAt = localEndedAt }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(localDate, body!.Date);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 21, 30, 0, TimeSpan.Zero), body.StartedAt);
        Assert.Equal(TimeSpan.Zero, body.StartedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 22, 45, 0, TimeSpan.Zero), body.EndedAt);
        Assert.Equal(TimeSpan.Zero, body.EndedAt!.Value.Offset);
    }

    [Fact]
    public async Task Update_with_explicit_null_clears_nullable_fields()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var originalDate = new DateOnly(2026, 1, 1);
        var originalStartedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var workout = await SeedWorkoutAsync(
            userId,
            originalDate,
            originalStartedAt,
            title: "Leg day",
            location: "Gym A",
            bodyweightKg: 78.4m,
            notes: "Heavy session",
            endedAt: new DateTimeOffset(2026, 1, 1, 9, 15, 0, TimeSpan.Zero));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
                new
                {
                    endedAt = (DateTimeOffset?)null,
                    title = (string?)null,
                    bodyweightKg = (decimal?)null,
                    location = (string?)null,
                    notes = (string?)null,
                }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(originalDate, body!.Date);
        Assert.Equal(originalStartedAt, body.StartedAt);
        Assert.Null(body.EndedAt);
        Assert.Null(body.Title);
        Assert.Null(body.BodyweightKg);
        Assert.Null(body.Location);
        Assert.Null(body.Notes);
    }

    [Fact]
    public async Task Update_with_null_required_field_returns_bad_request()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(
            userId,
            new DateOnly(2026, 1, 1),
            new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
                new { date = (DateOnly?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_without_a_token_returns_unauthorized()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/workouts/1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_for_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/workouts/{workout.Id}", tokenB));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_workout()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/workouts/{workout.Id}", token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Workouts.AnyAsync(w => w.Id == workout.Id));
    }
}
