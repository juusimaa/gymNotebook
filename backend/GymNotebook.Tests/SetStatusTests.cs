using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/006 set status: the column default for sets written before statuses existed,
// the page-state rule every set write route enforces (SetStatusRules), and the finish and
// reopen transitions on PATCH /workouts/{id} (research R3). Pages and sets are seeded
// straight through AppDbContext, as in SetsTests, so each test starts from an exact state.
public class SetStatusTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // Seeded rather than registered, for the same rate-limit reason as SetsTests.
    private async Task<(string Token, int UserId)> RegisterAndGetUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new User
        {
            DisplayName = "Test user",
            Email = $"{Guid.NewGuid():N}@example.test",
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple"),
            TokenVersion = 0,
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);
        return (token, user.Id);
    }

    // An in-progress page unless endedAt is given, in which case it is finished.
    private async Task<Workout> SeedWorkoutAsync(int userId, DateTimeOffset? endedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workout = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 1, 1),
            StartedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero),
            EndedAt = endedAt,
        };
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();
        return workout;
    }

    // One "Back Squat" block holding one set per status given, numbered in order.
    private async Task<List<SetEntry>> SeedSetsAsync(int userId, int workoutId, params SetStatus[] statuses)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var exercise = new Exercise
        {
            UserId = userId,
            Name = "Back Squat",
            NormalizedName = ExerciseNameNormalizer.Normalize("Back Squat"),
        };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync();

        var block = new WorkoutExercise { WorkoutId = workoutId, ExerciseId = exercise.Id, Position = 0 };
        var setNumber = 1;
        foreach (var status in statuses)
        {
            block.SetEntries.Add(new SetEntry { SetNumber = setNumber++, Reps = 5, Weight = 100m, Status = status });
        }
        db.WorkoutExercises.Add(block);
        await db.SaveChangesAsync();
        return [.. block.SetEntries];
    }

    // The stored statuses of a page's sets, in page order, read from the database rather
    // than from a response so each test checks what was actually written.
    private async Task<List<SetStatus>> ReadStatusesAsync(int workoutId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await (
            from se in db.SetEntries.AsNoTracking()
            join we in db.WorkoutExercises.AsNoTracking() on se.WorkoutExerciseId equals we.Id
            where we.WorkoutId == workoutId
            orderby we.Position, se.SetNumber
            select se.Status)
            .ToListAsync();
    }

    private async Task<int> ReadRevisionAsync(int workoutId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Workouts.AsNoTracking().Where(w => w.Id == workoutId).Select(w => w.Revision).SingleAsync();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PutOneSetAsync(int workoutId, string token, string? status) =>
        SendAsync(HttpMethod.Put, $"/workouts/{workoutId}/exercises", token,
            new PutWorkoutExercisesRequest([new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(100m, 5, false, status)])]));

    private static async Task AssertInvalidSetStatusAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_set_status", error!.Code);
    }

    [Fact]
    public async Task Migration_ExistingSets_ReadAsDone()
    {
        // Arrange: a set inserted the way every pre-migration row was, without a status
        // column value, so only the column default can fill it in.
        var (_, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        await SeedSetsAsync(userId, workout.Id); // the block, with no sets yet
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blockId = await db.WorkoutExercises.Where(we => we.WorkoutId == workout.Id).Select(we => we.Id).SingleAsync();

            // Act
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO set_entries (workout_exercise_id, set_number, reps, is_warmup) VALUES ({blockId}, 1, 5, false)");
        }

        // Assert
        Assert.Equal([SetStatus.Done], await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PutExercises_OmittedStatusOnInProgressPage_SavesDone()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await PutOneSetAsync(workout.Id, token, status: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal("done", Assert.Single(Assert.Single(body!.Exercises).Sets).Status);
        Assert.Equal([SetStatus.Done], await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PutExercises_PlannedOnInProgressPage_Saves()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await PutOneSetAsync(workout.Id, token, "planned");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Planned], await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PutExercises_SkippedOnInProgressPage_Returns400InvalidSetStatus()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await PutOneSetAsync(workout.Id, token, "skipped");

        await AssertInvalidSetStatusAsync(response);
    }

    [Fact]
    public async Task PutExercises_PlannedOnFinishedPage_Returns400()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, endedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));

        var response = await PutOneSetAsync(workout.Id, token, "planned");

        await AssertInvalidSetStatusAsync(response);
    }

    [Fact]
    public async Task PutExercises_InvalidStatus_WritesNothing()
    {
        // Arrange: a page that already holds one done set; the bad status sits on the
        // second set of the payload, after a valid first one.
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        await SeedSetsAsync(userId, workout.Id, SetStatus.Done);
        var revisionBefore = await ReadRevisionAsync(workout.Id);

        // Act
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
            new PutWorkoutExercisesRequest([
                new PutWorkoutExerciseInput("Bench Press", [
                    new PutSetInput(60m, 8, false, "planned"),
                    new PutSetInput(60m, 8, false, "lifted"),
                ]),
            ]));

        // Assert: refused, and the page is exactly as it was, revision included.
        await AssertInvalidSetStatusAsync(response);
        Assert.Equal([SetStatus.Done], await ReadStatusesAsync(workout.Id));
        Assert.Equal(revisionBefore, await ReadRevisionAsync(workout.Id));
    }

    [Fact]
    public async Task AddSet_SkippedOnInProgressPage_Returns400InvalidSetStatus()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);

        var response = await SendAsync(HttpMethod.Post, $"/workouts/{workout.Id}/sets", token,
            new CreateSetRequest("Back Squat", 100m, 5, false, Status: "skipped"));

        await AssertInvalidSetStatusAsync(response);
        Assert.Empty(await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PatchSet_WithoutStatus_KeepsStatus()
    {
        // Arrange: a planned set, edited by a client that doesn't know about statuses.
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var set = Assert.Single(await SeedSetsAsync(userId, workout.Id, SetStatus.Planned));

        // Act
        var response = await SendAsync(HttpMethod.Patch, $"/workouts/{workout.Id}/sets/{set.Id}", token,
            new UpdateSetRequest(105m, 5, false));

        // Assert: the weight changed, the status didn't.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetWriteResponse>();
        Assert.Equal(105m, body!.Weight);
        Assert.Equal("planned", body.Status);
        Assert.Equal([SetStatus.Planned], await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PatchSet_PlannedToDoneOnInProgressPage_Saves()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        var set = Assert.Single(await SeedSetsAsync(userId, workout.Id, SetStatus.Planned));

        var response = await SendAsync(HttpMethod.Patch, $"/workouts/{workout.Id}/sets/{set.Id}", token,
            new UpdateSetRequest(100m, 5, false, Status: "done"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Done], await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PatchWorkout_Finish_TurnsPlannedIntoSkipped()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        await SeedSetsAsync(userId, workout.Id, SetStatus.Done, SetStatus.Planned);

        var response = await SendAsync(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
            new { endedAt = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(["done", "skipped"], Assert.Single(body!.Exercises).Sets.Select(s => s.Status));
        Assert.Equal([SetStatus.Done, SetStatus.Skipped], await ReadStatusesAsync(workout.Id));
    }

    [Fact]
    public async Task PatchWorkout_Reopen_TurnsSkippedIntoPlanned()
    {
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, endedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await SeedSetsAsync(userId, workout.Id, SetStatus.Done, SetStatus.Skipped);

        var response = await SendAsync(HttpMethod.Patch, $"/workouts/{workout.Id}", token,
            new { endedAt = (DateTimeOffset?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Done, SetStatus.Planned], await ReadStatusesAsync(workout.Id));
    }

    // US3 (research R3): a done set's figures can be corrected and it stays done. The
    // editor resends the whole page with each set's status, so the changed values arrive
    // together with "done".
    [Fact]
    public async Task PutExercises_ChangedValuesOnDoneSet_KeepsDone()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId);
        await SeedSetsAsync(userId, workout.Id, SetStatus.Done);

        // Act
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
            new PutWorkoutExercisesRequest([
                new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(102.5m, 4, false, "done")]),
            ]));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var set = Assert.Single(Assert.Single((await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>())!.Exercises).Sets);
        Assert.Equal(102.5m, set.Weight);
        Assert.Equal(4, set.Reps);
        Assert.Equal("done", set.Status);
        Assert.Equal([SetStatus.Done], await ReadStatusesAsync(workout.Id));
    }

    // FR-010a: a forgotten tap is fixed after the session, on the finished page.
    [Fact]
    public async Task PutExercises_SkippedToDoneOnFinishedPage_Saves()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, endedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await SeedSetsAsync(userId, workout.Id, SetStatus.Done, SetStatus.Skipped);

        // Act
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
            new PutWorkoutExercisesRequest([
                new PutWorkoutExerciseInput("Back Squat", [
                    new PutSetInput(100m, 5, false, "done"),
                    new PutSetInput(100m, 5, false, "done"),
                ]),
            ]));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Done, SetStatus.Done], await ReadStatusesAsync(workout.Id));
    }

    // A skipped set can be removed like any row: the page is saved without it.
    [Fact]
    public async Task PutExercises_RemoveSkippedSetOnFinishedPage_Saves()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var workout = await SeedWorkoutAsync(userId, endedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await SeedSetsAsync(userId, workout.Id, SetStatus.Done, SetStatus.Skipped);

        // Act
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{workout.Id}/exercises", token,
            new PutWorkoutExercisesRequest([
                new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(100m, 5, false, "done")]),
            ]));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Done], await ReadStatusesAsync(workout.Id));
    }
}
