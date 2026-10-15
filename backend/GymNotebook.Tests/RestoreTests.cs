using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

namespace GymNotebook.Tests;

[Collection("Lifecycle")]
public class RestoreTests(TwoHostGymNotebookFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTimeOffset _started = new(2026, 1, 2, 8, 0, 0, TimeSpan.Zero);
    private sealed record RoundTripRow(DateOnly Date, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
        string? Title, string? Location, string? Notes, decimal? BodyweightKg,
        int Position, string ExerciseName, bool IsBodyweight,
        int SetNumber, decimal? Weight, int Reps, bool IsWarmup);

    private static object File(
        object[]? exercises = null, object[]? workouts = null,
        object[]? blocks = null, object[]? sets = null, int version = 1,
        object? privacyRecords = null) => new
        {
            formatVersion = version,
            account = new { id = 999999, email = "foreign@example.test" },
            privacyRecords,
            exercises = exercises ?? [new { id = 101, userId = 999999, name = "Squat", isBodyweight = false }],
            workouts = workouts ?? [new { id = 201, userId = 999999, date = "2026-01-02", startedAt = _started, endedAt = _started.AddHours(1), title = "Leg day", location = "Gym", notes = "Good", bodyweightKg = 82.5m }],
            workoutExercises = blocks ?? [new { id = 301, workoutId = 201, exerciseId = 101, position = 0 }],
            sets = sets ?? [new { id = 401, workoutExerciseId = 301, setNumber = 1, weight = 100m, reps = 5, isWarmup = false }],
        };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object file) =>
        client.PostAsJsonAsync("/account/restore", file);

    [Fact]
    public async Task Restore_IntoEmptyAccount_AddsEverything()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = await response.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Equal(1, result?.PagesAdded);
        Assert.Equal(1, result?.SetsAdded);
        Assert.Equal(["Squat"], result!.ExercisesCreated);
        await using var db = fixture.NewContext();
        var workout = await db.Workouts.SingleAsync(w => w.UserId == userId);
        Assert.NotEqual(201, workout.Id);
        Assert.Equal("Leg day", workout.Title);
        Assert.Equal(82.5m, workout.BodyweightKg);
        var exercise = await db.Exercises.SingleAsync(e => e.UserId == userId);
        Assert.NotEqual(101, exercise.Id);
        Assert.Equal(1, await db.SetEntries.CountAsync(s => db.WorkoutExercises.Any(b => b.Id == s.WorkoutExerciseId && b.WorkoutId == workout.Id)));
    }

    [Fact]
    public async Task Restore_SameFileTwice_AddsNothing()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, File())).StatusCode);

        using var second = await PostAsync(client, File());

        var result = await second.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Equal(0, result?.PagesAdded);
        Assert.Equal(1, result?.PagesAlreadyPresent);
        Assert.Empty(result?.ExercisesCreated ?? []);
        await using var db = fixture.NewContext();
        Assert.Equal(1, await db.Workouts.CountAsync(w => w.UserId == userId));
    }

    [Fact]
    public async Task Restore_ExistingStartedAt_SkipsPageUntouched()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        await using (var db = fixture.NewContext())
        {
            db.Workouts.Add(new Workout { UserId = userId, Date = new DateOnly(2026, 1, 2), StartedAt = _started, Title = "Original" });
            await db.SaveChangesAsync();
        }
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verify = fixture.NewContext();
        Assert.Equal("Original", (await verify.Workouts.SingleAsync(w => w.UserId == userId)).Title);
        Assert.Equal(0, await verify.Exercises.CountAsync(e => e.UserId == userId));
    }

    [Fact]
    public async Task Restore_MatchingName_UsesExistingExerciseAndKeepsClassification()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        int exerciseId;
        await using (var db = fixture.NewContext())
        {
            var exercise = new Exercise { UserId = userId, Name = "  SQUAT ", NormalizedName = "squat", IsBodyweight = true };
            db.Exercises.Add(exercise);
            await db.SaveChangesAsync();
            exerciseId = exercise.Id;
        }
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        var result = await response.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Contains("Squat", result!.ClassificationKept);
        await using var verify = fixture.NewContext();
        Assert.Equal(exerciseId, (await verify.WorkoutExercises.SingleAsync(b => verify.Workouts.Any(w => w.Id == b.WorkoutId && w.UserId == userId))).ExerciseId);
        Assert.True((await verify.Exercises.SingleAsync(e => e.Id == exerciseId)).IsBodyweight);
    }

    [Fact]
    public async Task Restore_UnusedExercise_NotCreated()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = File(exercises:
        [
            new { id = 101, name = "Squat", isBodyweight = false },
            new { id = 102, name = "Unused", isBodyweight = false },
        ]);

        using var response = await PostAsync(client, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = fixture.NewContext();
        Assert.Equal(1, await db.Exercises.CountAsync(e => e.UserId == userId));
    }

    [Theory]
    [InlineData("broken_reference")]
    [InlineData("invalid_value")]
    [InlineData("unsupported_version")]
    public async Task Restore_InvalidFile_Returns400AndWritesNothing(string reason)
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = reason switch
        {
            "broken_reference" => File(blocks: [new { id = 301, workoutId = 999999, exerciseId = 101, position = 0 }]),
            "invalid_value" => File(sets: [new { id = 401, workoutExerciseId = 301, setNumber = 1, weight = -1m, reps = 5, isWarmup = false }]),
            _ => File(version: 3),
        };

        using var response = await PostAsync(client, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, (await response.Content.ReadFromJsonAsync<RestoreError>())?.Reason);
        await using var db = fixture.NewContext();
        Assert.Equal(0, await db.Workouts.CountAsync(w => w.UserId == userId));
        Assert.Equal(0, await db.Exercises.CountAsync(e => e.UserId == userId));
    }

    [Fact]
    public async Task Restore_InvalidDate_ReturnsInvalidValueWithoutWriting()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = File(workouts: [new { id = 201, date = "not-a-date", startedAt = _started }]);

        using var response = await PostAsync(client, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_value", (await response.Content.ReadFromJsonAsync<RestoreError>())?.Reason);
        await using var db = fixture.NewContext();
        Assert.Equal(0, await db.Workouts.CountAsync(w => w.UserId == userId));
    }

    [Fact]
    public async Task Restore_ForeignIds_NeverReachOtherUsers()
    {
        await using var host = fixture.CreateHost();
        var sourceId = await fixture.SeedUserAsync();
        var callerId = await fixture.SeedUserAsync();
        await using (var db = fixture.NewContext())
        {
            db.Workouts.Add(new Workout { UserId = sourceId, Date = new DateOnly(2026, 1, 2), StartedAt = _started });
            await db.SaveChangesAsync();
        }
        using var client = host.ClientFor(callerId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verify = fixture.NewContext();
        Assert.Equal(1, await verify.Workouts.CountAsync(w => w.UserId == sourceId));
        Assert.Equal(1, await verify.Workouts.CountAsync(w => w.UserId == callerId));
        Assert.Equal(1, await verify.Exercises.CountAsync(e => e.UserId == callerId));
    }

    [Fact]
    public async Task Restore_NoConsentFlagOn_DropsOptionalDetailsAndFileConsent()
    {
        await using var host = fixture.CreateHost(new Dictionary<string, string?> { ["PRIVACY_LIFECYCLE_ENABLED"] = "true" });
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File(privacyRecords: new { optionalDetailsConsent = new { statementVersion = "fake" } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<RestoreResponse>())?.OptionalDetailsDropped);
        await using var db = fixture.NewContext();
        var workout = await db.Workouts.SingleAsync(w => w.UserId == userId);
        Assert.Null(workout.Title);
        Assert.Null(workout.Location);
        Assert.Null(workout.Notes);
        Assert.Null(workout.BodyweightKg);
        Assert.Null((await db.Users.SingleAsync(u => u.Id == userId)).OptionalDetailsConsentVersion);
    }

    [Fact]
    public async Task Restore_ConsentFlagOn_PreservesOptionalDetails()
    {
        await using var host = fixture.CreateHost(new Dictionary<string, string?> { ["PRIVACY_LIFECYCLE_ENABLED"] = "true" });
        var userId = await fixture.SeedUserAsync();
        await using (var db = fixture.NewContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.OptionalDetailsConsentVersion = "v1";
            user.OptionalDetailsConsentedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<RestoreResponse>())?.OptionalDetailsDropped);
        await using var verify = fixture.NewContext();
        Assert.Equal("Leg day", (await verify.Workouts.SingleAsync(w => w.UserId == userId)).Title);
    }

    [Fact]
    public async Task Restore_EmptyPage_PreservesPageWithoutCreatingExercises()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File(blocks: [], sets: []));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = fixture.NewContext();
        Assert.Equal(1, await db.Workouts.CountAsync(w => w.UserId == userId));
        Assert.Equal(0, await db.Exercises.CountAsync(e => e.UserId == userId));
    }

    [Fact]
    public async Task Restore_TooLarge_Returns413()
    {
        await using var host = fixture.CreateHost(new Dictionary<string, string?> { ["Restore:MaxBytes"] = "128" },
            kestrel: options => options.Listen(IPAddress.Loopback, 0));
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Restore_Concurrent_Returns429()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, @id)", connection, transaction))
        {
            command.Parameters.AddWithValue("ns", NotebookRestore.RestoreLockNamespace);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("restore_in_progress", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Restore_RateLimit_Returns429WithRetryAfter()
    {
        await using var host = fixture.CreateHost(new Dictionary<string, string?> { ["RestoreRateLimit:PermitLimit"] = "1" });
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, File())).StatusCode);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Restore_NonJson_Returns415()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);

        using var response = await client.PostAsync("/account/restore", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Restore_ReferenceNotebook_RoundTripsWithin60Seconds()
    {
        await using var host = fixture.CreateHost();
        var sourceId = await fixture.SeedUserAsync();
        var targetId = await fixture.SeedUserAsync();
        await fixture.SeedReferenceNotebookAsync(sourceId);
        // Exercise every exported notebook field, including null weight and warm-up.
        await fixture.ExecuteAsync("UPDATE workouts SET title = 'Page ' || id, location = 'Gym', notes = 'Reference', bodyweight_kg = 82.25 WHERE user_id = @id", sourceId);
        await fixture.ExecuteAsync("UPDATE exercises SET is_bodyweight = (id % 2 = 0) WHERE user_id = @id", sourceId);
        await fixture.ExecuteAsync("UPDATE set_entries s SET weight = CASE WHEN s.set_number = 1 THEN NULL ELSE 100.25 END, reps = s.set_number, is_warmup = (s.set_number = 1) FROM workout_exercises b JOIN workouts w ON w.id = b.workout_id WHERE s.workout_exercise_id = b.id AND w.user_id = @id", sourceId);
        using var sourceClient = host.ClientFor(sourceId);
        using var targetClient = host.ClientFor(targetId);
        using var export = await ExportTestSupport.ExportAsync(sourceClient);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var bytes = await export.Content.ReadAsByteArrayAsync();

        var timer = Stopwatch.StartNew();
        using var restore = await targetClient.PostAsync("/account/restore",
            new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } });
        timer.Stop();

        output.WriteLine($"restore {timer.Elapsed.TotalSeconds:F1} s; payload {bytes.Length / 1024.0 / 1024.0:F1} MiB");
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var result = await restore.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Equal(1000, result?.PagesAdded);
        Assert.Equal(100_000, result?.SetsAdded);
        await using var db = fixture.NewContext();
        Assert.Equal(1000, await db.Workouts.CountAsync(w => w.UserId == targetId));
        Assert.Equal(10, await db.Exercises.CountAsync(e => e.UserId == targetId));
        Assert.Equal(10_000, await db.WorkoutExercises.CountAsync(b => db.Workouts.Any(w => w.Id == b.WorkoutId && w.UserId == targetId)));
        Assert.Equal(100_000, await db.SetEntries.CountAsync(s => db.WorkoutExercises.Any(b => b.Id == s.WorkoutExerciseId && db.Workouts.Any(w => w.Id == b.WorkoutId && w.UserId == targetId))));
        Assert.Equal(await ReadRowsAsync(db, sourceId), await ReadRowsAsync(db, targetId));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(60), $"restore took {timer.Elapsed}");
    }

    // specs/006 backup v2 (research R10, R11): a version 2 file with one finished page
    // (a done set and a skipped one), one prepared page (a planned set) and one template.
    // Each argument replaces its array whole.
    private static object V2File(
        object[]? workouts = null, object[]? sets = null,
        object[]? templates = null, object[]? templateExercises = null, object[]? templateSets = null) => new
        {
            formatVersion = 2,
            exercises = new object[]
            {
                new { id = 101, userId = 999999, name = "Squat", isBodyweight = false },
                new { id = 102, userId = 999999, name = "Bench Press", isBodyweight = false },
            },
            workouts = workouts ??
            [
                new { id = 201, userId = 999999, date = (string?)"2026-01-02", startedAt = (DateTimeOffset?)_started, endedAt = (DateTimeOffset?)_started.AddHours(1), title = (string?)null, location = (string?)null, notes = (string?)null, bodyweightKg = (decimal?)null },
                new { id = 202, userId = 999999, date = (string?)null, startedAt = (DateTimeOffset?)null, endedAt = (DateTimeOffset?)null, title = (string?)null, location = (string?)null, notes = (string?)null, bodyweightKg = (decimal?)null },
            ],
            workoutExercises = new object[]
            {
                new { id = 301, workoutId = 201, exerciseId = 101, position = 0 },
                new { id = 302, workoutId = 202, exerciseId = 102, position = 0 },
            },
            sets = sets ??
            [
                new { id = 401, workoutExerciseId = 301, setNumber = 1, weight = 100m, reps = 5, isWarmup = false, status = "done" },
                new { id = 402, workoutExerciseId = 301, setNumber = 2, weight = 100m, reps = 5, isWarmup = false, status = "skipped" },
                new { id = 403, workoutExerciseId = 302, setNumber = 1, weight = 80m, reps = 5, isWarmup = false, status = "planned" },
            ],
            templates = templates ?? [new { id = 501, userId = 999999, name = "Push A", createdAt = _started }],
            templateExercises = templateExercises ?? [new { id = 601, templateId = 501, exerciseId = 102, position = 0 }],
            templateSets = templateSets ?? [new { id = 701, templateExerciseId = 601, setNumber = 1, weight = 60m, reps = 8, isWarmup = true }],
        };

    private static async Task AssertNothingWrittenAsync(GymNotebook.Api.Data.AppDbContext db, int userId)
    {
        Assert.Equal(0, await db.Workouts.CountAsync(w => w.UserId == userId));
        Assert.Equal(0, await db.Exercises.CountAsync(e => e.UserId == userId));
        Assert.Equal(0, await db.Templates.CountAsync(t => t.UserId == userId));
    }

    [Fact]
    public async Task Restore_V2RoundTrip_RecreatesPreparedSkippedAndTemplates()
    {
        // Arrange: a source account with a finished page holding a skipped set, a prepared
        // page holding a planned one, and a template; exported as version 2.
        await using var host = fixture.CreateHost();
        var sourceId = await fixture.SeedUserAsync();
        var targetId = await fixture.SeedUserAsync();
        await using (var seed = fixture.NewContext())
        {
            var squat = new Exercise { UserId = sourceId, Name = "Squat", NormalizedName = "squat" };
            seed.Exercises.Add(squat);
            await seed.SaveChangesAsync();
            var finished = new Workout { UserId = sourceId, Date = new DateOnly(2026, 1, 2), StartedAt = _started, EndedAt = _started.AddHours(1) };
            var block = new WorkoutExercise { ExerciseId = squat.Id, Position = 0 };
            block.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = 100m, Reps = 5, Status = SetStatus.Done });
            block.SetEntries.Add(new SetEntry { SetNumber = 2, Weight = 100m, Reps = 5, Status = SetStatus.Skipped });
            finished.WorkoutExercises.Add(block);
            var prepared = new Workout { UserId = sourceId };
            var plannedBlock = new WorkoutExercise { ExerciseId = squat.Id, Position = 0 };
            plannedBlock.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = 105m, Reps = 5, Status = SetStatus.Planned });
            prepared.WorkoutExercises.Add(plannedBlock);
            seed.Workouts.AddRange(finished, prepared);
            var template = new Template { UserId = sourceId, Name = "Legs", NormalizedName = "legs" };
            var templateBlock = new TemplateExercise { ExerciseId = squat.Id, Position = 0 };
            templateBlock.Sets.Add(new TemplateSet { SetNumber = 1, Weight = 60m, Reps = 8, IsWarmup = true });
            template.Exercises.Add(templateBlock);
            seed.Templates.Add(template);
            await seed.SaveChangesAsync();
        }
        using var sourceClient = host.ClientFor(sourceId);
        using var targetClient = host.ClientFor(targetId);
        using var export = await ExportTestSupport.ExportAsync(sourceClient);
        var bytes = await export.Content.ReadAsByteArrayAsync();

        // Act
        using var restore = await targetClient.PostAsync("/account/restore",
            new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } });

        // Assert: the counts, then each kind of row as it was in the source.
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var result = (await restore.Content.ReadFromJsonAsync<RestoreResponse>())!;
        Assert.Equal((1, 1, 1, 0, 3), (result.PagesAdded, result.PreparedPagesAdded, result.TemplatesAdded, result.TemplatesAlreadyPresent, result.SetsAdded));
        await using var db = fixture.NewContext();
        var statuses = await (
            from w in db.Workouts
            join b in db.WorkoutExercises on w.Id equals b.WorkoutId
            join s in db.SetEntries on b.Id equals s.WorkoutExerciseId
            where w.UserId == targetId
            orderby w.StartedAt == null, s.SetNumber
            select new { Prepared = w.StartedAt == null, w.Date, s.Weight, s.Status })
            .ToListAsync();
        Assert.Equal(
            [(false, 100m, SetStatus.Done), (false, 100m, SetStatus.Skipped), (true, 105m, SetStatus.Planned)],
            statuses.Select(s => (s.Prepared, s.Weight!.Value, s.Status)));
        Assert.Null(statuses[2].Date);
        var restored = await db.Templates.Include(t => t.Exercises).ThenInclude(e => e.Sets).SingleAsync(t => t.UserId == targetId);
        Assert.Equal("Legs", restored.Name);
        var restoredSet = Assert.Single(Assert.Single(restored.Exercises).Sets);
        Assert.Equal((1, 60m, 8, true), (restoredSet.SetNumber, restoredSet.Weight!.Value, restoredSet.Reps, restoredSet.IsWarmup));
    }

    [Fact]
    public async Task Restore_V1File_SetsRestoreAsDone()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);

        using var response = await PostAsync(client, File());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Equal((0, 0, 0), (result!.PreparedPagesAdded, result.TemplatesAdded, result.TemplatesAlreadyPresent));
        await using var db = fixture.NewContext();
        var status = await db.SetEntries
            .Where(s => db.WorkoutExercises.Any(b => b.Id == s.WorkoutExerciseId && db.Workouts.Any(w => w.Id == b.WorkoutId && w.UserId == userId)))
            .Select(s => s.Status).SingleAsync();
        Assert.Equal(SetStatus.Done, status);
    }

    [Fact]
    public async Task Restore_PreparedTwice_AddsTwice()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, V2File())).StatusCode);

        using var second = await PostAsync(client, V2File());

        // The started page and the template are recognised; the plan is added again.
        var result = (await second.Content.ReadFromJsonAsync<RestoreResponse>())!;
        Assert.Equal((0, 1, 1, 0, 1), (result.PagesAdded, result.PagesAlreadyPresent, result.PreparedPagesAdded, result.TemplatesAdded, result.TemplatesAlreadyPresent));
        await using var db = fixture.NewContext();
        Assert.Equal(2, await db.Workouts.CountAsync(w => w.UserId == userId && w.StartedAt == null));
        Assert.Equal(1, await db.Workouts.CountAsync(w => w.UserId == userId && w.StartedAt != null));
        Assert.Equal(1, await db.Templates.CountAsync(t => t.UserId == userId));
    }

    [Fact]
    public async Task Restore_ExistingTemplateName_KeepsAccountsTemplate()
    {
        // Arrange: the account already has "push a", with its own single set.
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        int templateId;
        await using (var seed = fixture.NewContext())
        {
            var dips = new Exercise { UserId = userId, Name = "Dips", NormalizedName = "dips" };
            seed.Exercises.Add(dips);
            await seed.SaveChangesAsync();
            var template = new Template { UserId = userId, Name = "push a", NormalizedName = "push a" };
            var block = new TemplateExercise { ExerciseId = dips.Id, Position = 0 };
            block.Sets.Add(new TemplateSet { SetNumber = 1, Reps = 10 });
            template.Exercises.Add(block);
            seed.Templates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        using var client = host.ClientFor(userId);

        // Act: the file's "Push A" normalizes to the same name.
        using var response = await PostAsync(client, V2File());

        // Assert: counted as present, and the account's template is exactly as it was.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<RestoreResponse>())!;
        Assert.Equal((0, 1), (result.TemplatesAdded, result.TemplatesAlreadyPresent));
        await using var db = fixture.NewContext();
        var kept = await db.Templates.Include(t => t.Exercises).ThenInclude(e => e.Sets).SingleAsync(t => t.UserId == userId);
        Assert.Equal((templateId, "push a"), (kept.Id, kept.Name));
        Assert.Equal(10, Assert.Single(Assert.Single(kept.Exercises).Sets).Reps);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("2026-01-02", false, null)]
    [InlineData(null, false, true)]
    public async Task Restore_PreparedPageWithStartedAt_Returns400BackupInvalid(string? date, bool started, bool? ended)
    {
        // Arrange: a page with only part of a start (or an end without one).
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = V2File(workouts:
        [
            new { id = 201, userId = 999999, date = (string?)"2026-01-02", startedAt = (DateTimeOffset?)_started, endedAt = (DateTimeOffset?)null, title = (string?)null, location = (string?)null, notes = (string?)null, bodyweightKg = (decimal?)null },
            new { id = 202, userId = 999999, date, startedAt = started ? (DateTimeOffset?)_started.AddDays(1) : null, endedAt = ended == true ? (DateTimeOffset?)_started.AddDays(1) : null, title = (string?)null, location = (string?)null, notes = (string?)null, bodyweightKg = (decimal?)null },
        ], sets: [new { id = 401, workoutExerciseId = 301, setNumber = 1, weight = 100m, reps = 5, isWarmup = false, status = "done" }]);

        // Act
        using var response = await PostAsync(client, file);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new RestoreError("backup_invalid", "invalid_value"), await response.Content.ReadFromJsonAsync<RestoreError>());
        await using var db = fixture.NewContext();
        await AssertNothingWrittenAsync(db, userId);
    }

    [Fact]
    public async Task Restore_DoneSetOnPreparedPage_Returns400()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = V2File(sets:
        [
            new { id = 401, workoutExerciseId = 301, setNumber = 1, weight = 100m, reps = 5, isWarmup = false, status = "done" },
            new { id = 403, workoutExerciseId = 302, setNumber = 1, weight = 80m, reps = 5, isWarmup = false, status = "done" },
        ]);

        using var response = await PostAsync(client, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_value", (await response.Content.ReadFromJsonAsync<RestoreError>())?.Reason);
        await using var db = fixture.NewContext();
        await AssertNothingWrittenAsync(db, userId);
    }

    [Fact]
    public async Task Restore_DuplicateTemplateNamesInFile_Returns400()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = V2File(templates:
        [
            new { id = 501, userId = 999999, name = "Push A", createdAt = _started },
            new { id = 502, userId = 999999, name = " push  a", createdAt = _started },
        ]);

        using var response = await PostAsync(client, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_value", (await response.Content.ReadFromJsonAsync<RestoreError>())?.Reason);
        await using var db = fixture.NewContext();
        await AssertNothingWrittenAsync(db, userId);
    }

    [Fact]
    public async Task Restore_TemplateBrokenExerciseRef_Returns400()
    {
        await using var host = fixture.CreateHost();
        var userId = await fixture.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var file = V2File(templateExercises: [new { id = 601, templateId = 501, exerciseId = 999, position = 0 }]);

        using var response = await PostAsync(client, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("broken_reference", (await response.Content.ReadFromJsonAsync<RestoreError>())?.Reason);
        await using var db = fixture.NewContext();
        await AssertNothingWrittenAsync(db, userId);
    }

    private static Task<List<RoundTripRow>> ReadRowsAsync(GymNotebook.Api.Data.AppDbContext db, int userId) =>
        (from workout in db.Workouts.AsNoTracking()
         join block in db.WorkoutExercises on workout.Id equals block.WorkoutId
         join exercise in db.Exercises on block.ExerciseId equals exercise.Id
         join set in db.SetEntries on block.Id equals set.WorkoutExerciseId
         where workout.UserId == userId
         orderby workout.StartedAt, block.Position, set.SetNumber
         select new RoundTripRow(workout.Date!.Value, workout.StartedAt!.Value, workout.EndedAt,
             workout.Title, workout.Location, workout.Notes, workout.BodyweightKg,
             block.Position, exercise.Name, exercise.IsBodyweight,
             set.SetNumber, set.Weight, set.Reps, set.IsWarmup)).ToListAsync();
}
