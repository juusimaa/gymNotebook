using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Tests.SpikeR4;

// SPIKE A5: the two handlers that open their own transaction (PUT /workouts/{id}/exercises,
// POST /workouts/{id}/sets) must join the filter's transaction without nesting, and still
// roll back whole on failure.
[Collection("SpikeR4")]
public class A5TransactionTests(SpikeR4Database db)
{
    private static readonly Dictionary<string, string?> _guarded = new() { ["Spike:R4:Guard"] = "true" };

    [Fact]
    public async Task BeginTransaction_WhileTransactionOpen_Throws()
    {
        // Arrange: why T021 must remove the handlers' own BeginTransactionAsync calls.
        await using var context = db.NewContext();
        await using var outer = await context.Database.BeginTransactionAsync();

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.BeginTransactionAsync());
        Assert.Contains("transaction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutExercises_UnderFilter_ValidPayload_Returns200AndPersists()
    {
        // Arrange
        await using var host = db.CreateHost(new SpikeHooks(), _guarded);
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
        // Arrange: a workout that already has two blocks.
        await using var host = db.CreateHost(new SpikeHooks(), _guarded);
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
        await using var host = db.CreateHost(new SpikeHooks(), _guarded);
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
}
