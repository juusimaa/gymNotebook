using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// Guarded writes (research R4 → Q3/Q5, spike A5): the filter commits first and only then
// writes the response under a fresh delivery guard, and it commits only when the handler
// succeeded — so the two bulk handlers that used to own a transaction still roll back
// whole now that they run inside the filter's.
[Collection("Lifecycle")]
public class LifecycleWriteDeliveryTests(TwoHostGymNotebookFixture db)
{
    private static readonly object _newWorkout = new { date = "2026-03-01", startedAt = "2026-03-01T08:00:00Z" };

    [Fact]
    public async Task GuardedWrite_DeletionWinsBeforeDelivery_Returns401WithNoBody()
    {
        // Arrange: pause the host right after the write's commit, before delivery.
        var barrier = new CommitBarrier();
        await using var host = db.CreateHost(services: s => s.ConfigureDbContext<Api.Data.AppDbContext>(o => o.AddInterceptors(barrier)));
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act: while the write sits between commit and delivery, a deletion commits.
        var pending = client.PostAsJsonAsync("/workouts", _newWorkout);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var deletion = await db.HoldExclusiveAsync(userId))
        {
            await deletion.DeleteUserAndCommitAsync();
        }
        barrier.Release();
        var response = await pending;

        // Assert: no personal bytes after the account is gone (Q5).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GuardedWrite_PasswordChangeWinsBeforeDelivery_Returns401WithNoBodyAndKeepsCommit()
    {
        // Arrange
        var barrier = new CommitBarrier();
        await using var host = db.CreateHost(services: s => s.ConfigureDbContext<Api.Data.AppDbContext>(o => o.AddInterceptors(barrier)));
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act: a password change (token version bump) commits between commit and delivery.
        var pending = client.PostAsJsonAsync("/workouts", _newWorkout);
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var change = await db.HoldExclusiveAsync(userId))
        {
            await change.BumpTokenVersionAndCommitAsync();
        }
        barrier.Release();
        var response = await pending;

        // Assert: 401 with no body, yet the write did commit — the known consequence Q5
        // documents (a retry after signing in again can duplicate it).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        await using var context = db.NewContext();
        Assert.Equal(1, await context.Workouts.CountAsync(w => w.UserId == userId));
    }

    [Fact]
    public async Task PutExercises_UnderFilter_ValidPayload_Returns200AndPersists()
    {
        // Arrange
        await using var host = db.CreateHost();
        var userId = await db.SeedUserAsync();
        var workoutId = (await db.SeedNotebookAsync(userId, 1, 0, 0)).Single();
        using var client = host.ClientFor(userId);

        // Act: the same new name twice exercises the intermediate SaveChangesAsync path.
        var response = await client.PutAsJsonAsync($"/workouts/{workoutId}/exercises", new
        {
            exercises = new[]
            {
                new { exerciseName = "Squat", sets = new[] { new { weight = 100m, reps = 5, isWarmup = false } } },
                new { exerciseName = "Bench", sets = new[] { new { weight = 80m, reps = 5, isWarmup = false } } },
                new { exerciseName = "squat", sets = new[] { new { weight = 90m, reps = 8, isWarmup = false } } },
            },
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = db.NewContext();
        Assert.Equal(3, await context.WorkoutExercises.CountAsync(we => we.WorkoutId == workoutId));
        Assert.Equal(2, await context.Exercises.CountAsync(e => e.UserId == userId));
    }

    [Fact]
    public async Task PutExercises_UnderFilter_FailsMidway_RollsBackWhole()
    {
        // Arrange: a workout that already has two blocks of three sets.
        await using var host = db.CreateHost();
        var userId = await db.SeedUserAsync();
        var workoutId = (await db.SeedNotebookAsync(userId, 1, 2, 3)).Single();
        using var client = host.ClientFor(userId);

        // Act: the handler deletes the old blocks and saves, creates "Deadlift" and saves,
        // then hits the blank name and returns 400.
        var response = await client.PutAsJsonAsync($"/workouts/{workoutId}/exercises", new
        {
            exercises = new[]
            {
                new { exerciseName = "Deadlift", sets = Array.Empty<object>() },
                new { exerciseName = "  ", sets = Array.Empty<object>() },
            },
        });

        // Assert: nothing of the partial work survived.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var context = db.NewContext();
        Assert.Equal(2, await context.WorkoutExercises.CountAsync(we => we.WorkoutId == workoutId));
        Assert.Equal(6, await context.SetEntries.CountAsync(se => context.WorkoutExercises.Any(we => we.Id == se.WorkoutExerciseId && we.WorkoutId == workoutId)));
        Assert.False(await context.Exercises.AnyAsync(e => e.UserId == userId && e.NormalizedName == "deadlift"));
    }

    [Fact]
    public async Task PostSet_UnderFilter_Returns201AndPersists()
    {
        // Arrange
        await using var host = db.CreateHost();
        var userId = await db.SeedUserAsync();
        var workoutId = (await db.SeedNotebookAsync(userId, 1, 0, 0)).Single();
        using var client = host.ClientFor(userId);

        // Act
        var response = await client.PostAsJsonAsync($"/workouts/{workoutId}/sets", new { exerciseName = "Row", weight = 60m, reps = 10, isWarmup = false });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var context = db.NewContext();
        Assert.Equal(1, await context.SetEntries.CountAsync(se => context.WorkoutExercises.Any(we => we.Id == se.WorkoutExerciseId && we.WorkoutId == workoutId)));
    }

    [Fact]
    public async Task PostSet_UnderFilter_FailsAfterCreatingExerciseAndBlock_RollsBackWhole()
    {
        // Arrange
        await using var host = db.CreateHost();
        var userId = await db.SeedUserAsync();
        var workoutId = (await db.SeedNotebookAsync(userId, 1, 0, 0)).Single();
        using var client = host.ClientFor(userId);

        // Act: the handler creates the exercise and its block (each saved), then the set's
        // insert fails in the database — 100000 overflows weight's numeric(6,2).
        var response = await client.PostAsJsonAsync($"/workouts/{workoutId}/sets", new { exerciseName = "Overflow", weight = 100000m, reps = 1, isWarmup = false });

        // Assert: neither the exercise nor the block survived the failure.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var context = db.NewContext();
        Assert.False(await context.Exercises.AnyAsync(e => e.UserId == userId));
        Assert.False(await context.WorkoutExercises.AnyAsync(we => we.WorkoutId == workoutId));
    }
}
