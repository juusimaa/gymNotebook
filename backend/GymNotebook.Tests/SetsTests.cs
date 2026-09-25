using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// The four routes milestone 4's last PR adds: the bulk session write
// (PUT /workouts/{id}/exercises) and the three incremental set routes under
// /workouts/{id}/sets. Blocks and sets still have no other creation endpoint, so the
// starting state for the incremental tests is seeded straight through AppDbContext,
// the same way WorkoutTests and ExerciseTests do it.
public class SetsTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
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

    private async Task<Workout> SeedWorkoutAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workout = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 1, 1),
            StartedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero),
        };
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();
        return workout;
    }

    private async Task<Exercise> SeedExerciseAsync(int userId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exercise = new Exercise
        {
            UserId = userId,
            Name = name,
            NormalizedName = ExerciseNameNormalizer.Normalize(name),
        };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync();
        return exercise;
    }

    private async Task<(WorkoutExercise Block, List<SetEntry> Sets)> SeedBlockWithSetsAsync(
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

        var created = new List<SetEntry>();

        foreach (var (setNumber, reps, weight, isWarmup) in sets)
        {
            var set = new SetEntry
            {
                WorkoutExerciseId = block.Id,
                SetNumber = setNumber,
                Reps = reps,
                Weight = weight,
                IsWarmup = isWarmup,
            };
            db.SetEntries.Add(set);
            created.Add(set);
        }

        await db.SaveChangesAsync();
        return (block, created);
    }

    // One helper for every authenticated verb, since GET/POST/PUT/PATCH/DELETE all need
    // the same bearer header attached and only sometimes need a body.
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

    private async Task<WorkoutDetailResponse> GetWorkoutAsync(int workoutId, string token)
    {
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/workouts/{workoutId}", token));
        return (await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>())!;
    }

    // ---------------------------------------------------------------------------------
    // Auth: all four routes sit in the /workouts group, which is behind
    // .RequireAuthorization() — these prove none of them slipped out of it.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_exercises_without_a_token_returns_unauthorized()
    {
        var response = await _client.PutAsJsonAsync(
            "/workouts/1/exercises",
            new PutWorkoutExercisesRequest([]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Add_set_without_a_token_returns_unauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/workouts/1/sets",
            new CreateSetRequest("Back Squat", 100m, 5, false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_set_without_a_token_returns_unauthorized()
    {
        var response = await _client.PatchAsync(
            "/workouts/1/sets/1",
            JsonContent.Create(new UpdateSetRequest(100m, 5, false)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_set_without_a_token_returns_unauthorized()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/workouts/1/sets/1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------
    // PUT /workouts/{id}/exercises — the bulk session write.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_exercises_for_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", tokenB,
                new PutWorkoutExercisesRequest([new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(100m, 5, false)])])));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Replace_exercises_removes_the_previous_blocks_and_their_sets()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var (oldBlock, oldSets) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true),
            (SetNumber: 2, Reps: 3, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
                new PutWorkoutExercisesRequest([new PutWorkoutExerciseInput("Bench Press", [new PutSetInput(40m, 8, false)])])));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();

        var block = Assert.Single(body!.Exercises);
        Assert.Equal("Bench Press", block.ExerciseName);
        Assert.Single(block.Sets);

        // Replace means gone, not orphaned: the old block's row and — through the
        // database-level cascade — its sets are no longer there at all.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkoutExercises.AnyAsync(we => we.Id == oldBlock.Id));
        Assert.False(await db.SetEntries.AnyAsync(se => oldSets.Select(s => s.Id).Contains(se.Id)));
    }

    [Fact]
    public async Task Replace_exercises_reuses_an_existing_exercise_and_creates_a_missing_one()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var benchPress = await SeedExerciseAsync(userId, "Bench Press");

        // The first name differs from the seeded one in casing and whitespace only, so
        // normalization has to resolve it onto the existing row rather than inserting a
        // second one and colliding with the unique (user_id, normalized_name) index.
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
                new PutWorkoutExercisesRequest([
                    new PutWorkoutExerciseInput("  bench   PRESS ", [new PutSetInput(40m, 8, false)]),
                    new PutWorkoutExerciseInput("Deadlift", [new PutSetInput(140m, 3, false)]),
                ])));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();

        Assert.Equal(benchPress.Id, body!.Exercises[0].ExerciseId);
        Assert.Equal("Bench Press", body.Exercises[0].ExerciseName);
        Assert.Equal("Deadlift", body.Exercises[1].ExerciseName);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Exercises.CountAsync(e => e.UserId == userId));
    }

    [Fact]
    public async Task Replace_exercises_numbers_positions_and_sets_from_array_order()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
                new PutWorkoutExercisesRequest([
                    new PutWorkoutExerciseInput("Back Squat", [
                        new PutSetInput(60m, 5, true),
                        new PutSetInput(80m, 5, false),
                        new PutSetInput(90m, 3, false),
                    ]),
                    new PutWorkoutExerciseInput("Bench Press", [new PutSetInput(40m, 8, false)]),
                ])));

        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();

        // Array order is the session's order, full stop — the client never sends a
        // position or a set number, and re-reading it back must give the same sequence.
        Assert.Equal(new[] { "Back Squat", "Bench Press" }, body!.Exercises.Select(e => e.ExerciseName));
        Assert.Equal(new[] { 1, 2, 3 }, body.Exercises[0].Sets.Select(s => s.SetNumber));
        Assert.Equal(new decimal?[] { 60m, 80m, 90m }, body.Exercises[0].Sets.Select(s => s.Weight));
        Assert.True(body.Exercises[0].Sets[0].IsWarmup);

        var reread = await GetWorkoutAsync(workout.Id, token);
        Assert.Equal(new[] { "Back Squat", "Bench Press" }, reread.Exercises.Select(e => e.ExerciseName));
    }

    [Fact]
    public async Task Replace_exercises_keeps_two_blocks_when_the_same_exercise_appears_twice()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        // "Came back to squats later in the session" — the case WorkoutExercise exists to
        // make representable. Collapsing these into one block would lose the fact that
        // bench press happened in between.
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
                new PutWorkoutExercisesRequest([
                    new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(90m, 3, false)]),
                    new PutWorkoutExerciseInput("Bench Press", [new PutSetInput(40m, 8, false)]),
                    new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(95m, 2, false)]),
                ])));

        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();

        Assert.Equal(3, body!.Exercises.Count);
        Assert.Equal(new[] { "Back Squat", "Bench Press", "Back Squat" }, body.Exercises.Select(e => e.ExerciseName));

        // Two blocks, but one Exercise row — get-or-create has to see the name it just
        // created earlier in this same request.
        Assert.Equal(body.Exercises[0].ExerciseId, body.Exercises[2].ExerciseId);
        Assert.NotEqual(body.Exercises[0].Id, body.Exercises[2].Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Exercises.CountAsync(e => e.UserId == userId && e.NormalizedName == "back squat"));
    }

    [Fact]
    public async Task Replace_exercises_with_an_empty_list_clears_the_session()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
                new PutWorkoutExercisesRequest([])));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Empty(body!.Exercises);

        // The Exercise row survives: it's autocomplete history, not session content.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Exercises.AnyAsync(e => e.Id == backSquat.Id));
    }

    [Fact]
    public async Task Replace_exercises_rolls_back_whole_when_a_later_block_is_rejected()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var (oldBlock, oldSets) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true),
            (SetNumber: 2, Reps: 3, Weight: 90m, IsWarmup: false));

        // PLAN.md's testing section names this rule specifically: the bulk write rolls back
        // whole rather than half-applying. The blank name in the *second* block is rejected
        // only after the handler has already deleted the old blocks and created the
        // "Overhead Press" exercise, so a missing transaction would leave the session
        // wiped and a junk exercise behind.
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
                new PutWorkoutExercisesRequest([
                    new PutWorkoutExerciseInput("Overhead Press", [new PutSetInput(40m, 5, false)]),
                    new PutWorkoutExerciseInput("   ", [new PutSetInput(50m, 5, false)]),
                ])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The original session is exactly as it was.
        var detail = await GetWorkoutAsync(workout.Id, token);
        var block = Assert.Single(detail.Exercises);
        Assert.Equal("Back Squat", block.ExerciseName);
        Assert.Equal(new[] { 1, 2 }, block.Sets.Select(s => s.SetNumber));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkoutExercises.AnyAsync(we => we.Id == oldBlock.Id));
        Assert.Equal(2, await db.SetEntries.CountAsync(se => oldSets.Select(s => s.Id).Contains(se.Id)));

        // And the half-created exercise from the accepted first block is gone too.
        Assert.False(await db.Exercises.AnyAsync(e => e.UserId == userId && e.NormalizedName == "overhead press"));
    }

    // ---------------------------------------------------------------------------------
    // POST /workouts/{id}/sets — the incremental append.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Add_set_for_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, $"/workouts/{workout.Id}/sets", tokenB,
                new CreateSetRequest("Back Squat", 100m, 5, false)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Add_set_creates_the_block_when_the_exercise_is_new_to_the_workout()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, $"/workouts/{workout.Id}/sets", token,
                new CreateSetRequest("Back Squat", 100m, 5, false)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // 201 without a Location header is deliberate: there is no GET route for a single
        // set, so there is nothing honest for it to point at.
        Assert.Null(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<SetEntryResponse>();
        Assert.Equal(1, body!.SetNumber);
        Assert.Equal(100m, body.Weight);

        var detail = await GetWorkoutAsync(workout.Id, token);
        var block = Assert.Single(detail.Exercises);
        Assert.Equal("Back Squat", block.ExerciseName);
        Assert.Single(block.Sets);
    }

    [Fact]
    public async Task Add_set_appends_to_an_existing_block_instead_of_starting_a_new_one()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true),
            (SetNumber: 2, Reps: 5, Weight: 80m, IsWarmup: false));

        // Different casing on purpose: the block is found via the normalized exercise
        // name, so "back squat" must land in the "Back Squat" block.
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, $"/workouts/{workout.Id}/sets", token,
                new CreateSetRequest("back squat", 90m, 3, false)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetEntryResponse>();

        // Next number in the block, assigned server-side — the request never mentioned one.
        Assert.Equal(3, body!.SetNumber);

        var detail = await GetWorkoutAsync(workout.Id, token);
        var block = Assert.Single(detail.Exercises);
        Assert.Equal(new[] { 1, 2, 3 }, block.Sets.Select(s => s.SetNumber));
    }

    [Fact]
    public async Task Add_set_appends_to_the_latest_block_when_the_exercise_has_two()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var benchPress = await SeedExerciseAsync(userId, "Bench Press");

        var (firstSquatBlock, _) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 90m, IsWarmup: false));
        await SeedBlockWithSetsAsync(workout.Id, benchPress.Id, position: 1,
            (SetNumber: 1, Reps: 8, Weight: 40m, IsWarmup: false));
        var (secondSquatBlock, _) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 2,
            (SetNumber: 1, Reps: 2, Weight: 95m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, $"/workouts/{workout.Id}/sets", token,
                new CreateSetRequest("Back Squat", 100m, 1, false)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetEntryResponse>();

        // The set the lifter is adding now belongs to the block they're currently on —
        // the later one. The earlier block is finished work and stays untouched.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.SetEntries.SingleAsync(se => se.Id == body!.Id);
        Assert.Equal(secondSquatBlock.Id, stored.WorkoutExerciseId);
        Assert.Equal(2, stored.SetNumber);
        Assert.Equal(1, await db.SetEntries.CountAsync(se => se.WorkoutExerciseId == firstSquatBlock.Id));
    }

    [Fact]
    public async Task Add_set_with_a_blank_exercise_name_returns_bad_request()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, $"/workouts/{workout.Id}/sets", token,
                new CreateSetRequest("   ", 100m, 5, false)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing was created — a blank name would otherwise take this user's one "" slot
        // in the unique (user_id, normalized_name) index.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Exercises.AnyAsync(e => e.UserId == userId));
    }

    [Fact]
    public async Task Add_set_accepts_a_null_weight_for_a_bodyweight_set()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, $"/workouts/{workout.Id}/sets", token,
                new CreateSetRequest("Pull-up", null, 10, false)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetEntryResponse>();
        Assert.Null(body!.Weight);
        Assert.Equal(10, body.Reps);
    }

    // ---------------------------------------------------------------------------------
    // PATCH and DELETE /workouts/{id}/sets/{setId} — the two-level ownership check.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Update_set_applies_all_three_fields_and_can_clear_the_weight()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var pullUp = await SeedExerciseAsync(userId, "Pull-up");
        var (_, sets) = await SeedBlockWithSetsAsync(workout.Id, pullUp.Id, position: 0,
            (SetNumber: 1, Reps: 8, Weight: 10m, IsWarmup: false));

        // Clearing the weight is the case that makes this PATCH non-sparse: null here has
        // to mean "this is a bodyweight set now", not "leave the weight alone".
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}/sets/{sets[0].Id}", token,
                new UpdateSetRequest(null, 12, true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetEntryResponse>();

        Assert.Null(body!.Weight);
        Assert.Equal(12, body.Reps);
        Assert.True(body.IsWarmup);

        // Server-owned fields are untouched.
        Assert.Equal(sets[0].Id, body.Id);
        Assert.Equal(1, body.SetNumber);
    }

    [Fact]
    public async Task Update_set_belonging_to_another_workout_returns_not_found()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workoutWithTheSet = await SeedWorkoutAsync(userId);
        var otherWorkout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var (_, sets) = await SeedBlockWithSetsAsync(workoutWithTheSet.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 90m, IsWarmup: false));

        // Both workouts are the caller's own, so the workout-level check passes and this
        // can only be caught by the second level: set ids are global, and pairing a real
        // one with the wrong workout must not be an edit.
        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{otherWorkout.Id}/sets/{sets[0].Id}", token,
                new UpdateSetRequest(999m, 1, false)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.SetEntries.SingleAsync(se => se.Id == sets[0].Id);
        Assert.Equal(90m, stored.Weight);
    }

    [Fact]
    public async Task Update_set_in_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA);
        var backSquat = await SeedExerciseAsync(userIdA, "Back Squat");
        var (_, sets) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}/sets/{sets[0].Id}", tokenB,
                new UpdateSetRequest(999m, 1, false)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_set_with_an_unknown_id_returns_not_found()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Patch, $"/workouts/{workout.Id}/sets/999999", token,
                new UpdateSetRequest(100m, 5, false)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_set_removes_only_that_set()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var (block, sets) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 60m, IsWarmup: true),
            (SetNumber: 2, Reps: 5, Weight: 80m, IsWarmup: false),
            (SetNumber: 3, Reps: 3, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/workouts/{workout.Id}/sets/{sets[1].Id}", token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detail = await GetWorkoutAsync(workout.Id, token);
        var remaining = Assert.Single(detail.Exercises);

        // The survivors keep their original numbers rather than being renumbered — the
        // client is holding ids for rows it didn't touch, and the block itself survives an
        // empty-able delete too.
        Assert.Equal(new[] { 1, 3 }, remaining.Sets.Select(s => s.SetNumber));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkoutExercises.AnyAsync(we => we.Id == block.Id));
    }

    [Fact]
    public async Task Delete_set_belonging_to_another_workout_returns_not_found()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workoutWithTheSet = await SeedWorkoutAsync(userId);
        var otherWorkout = await SeedWorkoutAsync(userId);
        var backSquat = await SeedExerciseAsync(userId, "Back Squat");
        var (_, sets) = await SeedBlockWithSetsAsync(workoutWithTheSet.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/workouts/{otherWorkout.Id}/sets/{sets[0].Id}", token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.SetEntries.AnyAsync(se => se.Id == sets[0].Id));
    }

    [Fact]
    public async Task Delete_set_in_another_users_workout_returns_not_found()
    {
        var (_, userIdA) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userIdA);
        var backSquat = await SeedExerciseAsync(userIdA, "Back Squat");
        var (_, sets) = await SeedBlockWithSetsAsync(workout.Id, backSquat.Id, position: 0,
            (SetNumber: 1, Reps: 5, Weight: 90m, IsWarmup: false));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/workouts/{workout.Id}/sets/{sets[0].Id}", tokenB));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.SetEntries.AnyAsync(se => se.Id == sets[0].Id));
    }
}
