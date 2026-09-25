using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Tests;

// specs/001 user story 3 (tasks.md T044): what the export contains. Account A is seeded
// with every supported field — Unicode, decimals, a local date that differs from the UTC
// date, nulls, an unused exercise, a repeated block and an unfinished workout — and B with
// canary values that must never appear in A's file. Every value is compared with what the
// database really holds, not with what the test meant to seed.
[Collection("Lifecycle")]
public class ExportTests(TwoHostGymNotebookFixture db)
{
    private const string CanaryB = "CANARY-B";

    [Fact]
    public async Task Export_SeededAccount_MatchesDatabaseFieldByField()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userA = await SeedFullAccountAsync();
        await SeedCanaryAccountAsync();
        using var client = host.ClientFor(userA);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);
        var text = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        var snapshotAt = DateTimeOffset.Parse(root.GetProperty("snapshotAt").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(snapshotAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        await using var context = db.NewContext();

        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userA);
        var account = root.GetProperty("account");
        Assert.Equal(user.Id, account.GetProperty("id").GetInt32());
        Assert.Equal(user.PrivacyAccountId, account.GetProperty("privacyAccountId").GetGuid());
        Assert.Equal(user.Username, account.GetProperty("username").GetString());
        Assert.Equal(user.CreatedAt, Instant(account.GetProperty("createdAt")));

        var exercises = await context.Exercises.AsNoTracking().Where(e => e.UserId == userA).OrderBy(e => e.Id).ToListAsync();
        var exportedExercises = root.GetProperty("exercises").EnumerateArray().ToList();
        Assert.Equal(3, exercises.Count); // includes the unused one
        Assert.Equal(exercises.Count, exportedExercises.Count);
        foreach (var (expected, actual) in exercises.Zip(exportedExercises))
        {
            Assert.Equal(expected.Id, actual.GetProperty("id").GetInt32());
            Assert.Equal(expected.UserId, actual.GetProperty("userId").GetInt32());
            Assert.Equal(expected.Name, actual.GetProperty("name").GetString());
            Assert.Equal(expected.IsBodyweight, actual.GetProperty("isBodyweight").GetBoolean());
            Assert.Equal(expected.CreatedAt, Instant(actual.GetProperty("createdAt")));
        }

        var workouts = await context.Workouts.AsNoTracking().Where(w => w.UserId == userA).OrderBy(w => w.Id).ToListAsync();
        var exportedWorkouts = root.GetProperty("workouts").EnumerateArray().ToList();
        Assert.Equal(2, workouts.Count);
        Assert.Equal(workouts.Count, exportedWorkouts.Count);
        foreach (var (expected, actual) in workouts.Zip(exportedWorkouts))
        {
            Assert.Equal(expected.Id, actual.GetProperty("id").GetInt32());
            Assert.Equal(expected.UserId, actual.GetProperty("userId").GetInt32());
            Assert.Equal(expected.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), actual.GetProperty("date").GetString());
            Assert.Equal(expected.StartedAt, Instant(actual.GetProperty("startedAt")));
            Assert.Equal(expected.EndedAt, NullableInstant(actual.GetProperty("endedAt")));
            Assert.Equal(expected.Title, NullableText(actual.GetProperty("title")));
            Assert.Equal(expected.Location, NullableText(actual.GetProperty("location")));
            Assert.Equal(expected.Notes, NullableText(actual.GetProperty("notes")));
            AssertDecimal(expected.BodyweightKg, actual.GetProperty("bodyweightKg"));
            Assert.Equal(expected.CreatedAt, Instant(actual.GetProperty("createdAt")));
        }
        // The unfinished workout keeps every nullable field present, as null.
        var unfinished = exportedWorkouts[1];
        foreach (var field in new[] { "endedAt", "title", "location", "notes", "bodyweightKg" })
        {
            Assert.Equal(JsonValueKind.Null, unfinished.GetProperty(field).ValueKind);
        }

        var workoutIds = workouts.Select(w => w.Id).ToList();
        var blocks = await context.WorkoutExercises.AsNoTracking()
            .Where(we => workoutIds.Contains(we.WorkoutId))
            .OrderBy(we => we.WorkoutId).ThenBy(we => we.Position).ThenBy(we => we.Id)
            .ToListAsync();
        var exportedBlocks = root.GetProperty("workoutExercises").EnumerateArray().ToList();
        Assert.Equal(4, blocks.Count);
        Assert.Equal(blocks.Count, exportedBlocks.Count);
        foreach (var (expected, actual) in blocks.Zip(exportedBlocks))
        {
            Assert.Equal(expected.Id, actual.GetProperty("id").GetInt32());
            Assert.Equal(expected.WorkoutId, actual.GetProperty("workoutId").GetInt32());
            Assert.Equal(expected.ExerciseId, actual.GetProperty("exerciseId").GetInt32());
            Assert.Equal(expected.Position, actual.GetProperty("position").GetInt32());
        }
        // The repeated exercise stays two separate blocks in the first workout.
        Assert.Equal(2, blocks.Count(b => b.WorkoutId == workouts[0].Id && b.ExerciseId == exercises[0].Id));

        var blockIds = blocks.Select(b => b.Id).ToList();
        var sets = await context.SetEntries.AsNoTracking()
            .Where(s => blockIds.Contains(s.WorkoutExerciseId))
            .OrderBy(s => s.WorkoutExerciseId).ThenBy(s => s.SetNumber).ThenBy(s => s.Id)
            .ToListAsync();
        var exportedSets = root.GetProperty("sets").EnumerateArray().ToList();
        Assert.Equal(6, sets.Count);
        Assert.Equal(sets.Count, exportedSets.Count);
        foreach (var (expected, actual) in sets.Zip(exportedSets))
        {
            Assert.Equal(expected.Id, actual.GetProperty("id").GetInt32());
            Assert.Equal(expected.WorkoutExerciseId, actual.GetProperty("workoutExerciseId").GetInt32());
            Assert.Equal(expected.SetNumber, actual.GetProperty("setNumber").GetInt32());
            AssertDecimal(expected.Weight, actual.GetProperty("weight"));
            Assert.Equal(expected.Reps, actual.GetProperty("reps").GetInt32());
            Assert.Equal(expected.IsWarmup, actual.GetProperty("isWarmup").GetBoolean());
        }

        // Every reference resolves within the file.
        var exportedExerciseIds = exportedExercises.Select(e => e.GetProperty("id").GetInt32()).ToHashSet();
        var exportedWorkoutIds = exportedWorkouts.Select(w => w.GetProperty("id").GetInt32()).ToHashSet();
        var exportedBlockIds = exportedBlocks.Select(b => b.GetProperty("id").GetInt32()).ToHashSet();
        Assert.All(exportedBlocks, b => Assert.Contains(b.GetProperty("workoutId").GetInt32(), exportedWorkoutIds));
        Assert.All(exportedBlocks, b => Assert.Contains(b.GetProperty("exerciseId").GetInt32(), exportedExerciseIds));
        Assert.All(exportedSets, s => Assert.Contains(s.GetProperty("workoutExerciseId").GetInt32(), exportedBlockIds));

        var privacy = root.GetProperty("privacyRecords");
        var acknowledgement = privacy.GetProperty("noticeAcknowledgement");
        Assert.Equal(user.AcknowledgedPrivacyNoticeVersion, acknowledgement.GetProperty("noticeVersion").GetString());
        Assert.Equal(user.PrivacyNoticeAcknowledgedAt, Instant(acknowledgement.GetProperty("acknowledgedAt")));
        Assert.Equal(JsonValueKind.Null, privacy.GetProperty("optionalDetailsConsent").ValueKind);

        // Nothing of B's, and no credential or revocation value, anywhere in the file.
        Assert.DoesNotContain(CanaryB, text);
        Assert.DoesNotContain(user.PasswordHash, text);
        foreach (var forbidden in new[] { "passwordHash", "tokenVersion", "signInSuspendedAt", "normalizedName", "token_version", "password_hash" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Export_EmptyAccount_ReturnsEmptyArraysAndNullAcknowledgement()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert: a valid file, not an error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        foreach (var collection in new[] { "exercises", "workouts", "workoutExercises", "sets" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(collection).ValueKind);
            Assert.Equal(0, root.GetProperty(collection).GetArrayLength());
        }
        Assert.Equal(userId, root.GetProperty("account").GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("privacyRecords").GetProperty("noticeAcknowledgement").ValueKind);
    }

    [Fact]
    public async Task Export_Success_HeadersMatchContract()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert: contracts/api.md → Export format version 1.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment; filename=\"gym-notebook-export.json\"", string.Join(", ", response.Content.Headers.GetValues("Content-Disposition")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Export_FieldGuide_DocumentsEveryExportedField()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await SeedFullAccountAsync();
        using var client = host.ClientFor(userId);

        // Act
        var response = await ExportTestSupport.ExportAsync(client);

        // Assert: every key of the document and of each record kind has a guide entry, so
        // a field can't be added to the export without explaining it.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var guide = root.GetProperty("fieldGuide");
        foreach (var topLevel in root.EnumerateObject().Where(p => p.Name != "fieldGuide"))
        {
            Assert.True(guide.TryGetProperty(topLevel.Name, out _), $"fieldGuide has no entry for {topLevel.Name}");
        }
        AssertDocumented(guide.GetProperty("account"), root.GetProperty("account"));
        AssertDocumented(guide.GetProperty("privacyRecords"), root.GetProperty("privacyRecords"));
        foreach (var collection in new[] { "exercises", "workouts", "workoutExercises", "sets" })
        {
            AssertDocumented(guide.GetProperty(collection), root.GetProperty(collection)[0]);
        }
    }

    private static void AssertDocumented(JsonElement guide, JsonElement record)
    {
        foreach (var field in record.EnumerateObject())
        {
            Assert.True(guide.TryGetProperty(field.Name, out var entry) && entry.ValueKind == JsonValueKind.String,
                $"fieldGuide has no explanation for {field.Name}");
        }
    }

    // Account A: two used exercises (one bodyweight) and one unused with a non-ASCII name;
    // a finished workout with every optional field, whose local date differs from its UTC
    // start date, and with the same exercise in two blocks; and an unfinished workout with
    // every optional field null. Plus a retained notice acknowledgement.
    private async Task<int> SeedFullAccountAsync()
    {
        var userId = await db.SeedUserAsync();
        await using var context = db.NewContext();

        var bench = new Exercise { UserId = userId, Name = "Bench press", NormalizedName = "bench press" };
        var pullUp = new Exercise { UserId = userId, Name = "Pull-up", NormalizedName = "pull-up", IsBodyweight = true };
        var unused = new Exercise { UserId = userId, Name = "Kyykky – äöå 💪", NormalizedName = "kyykky – äöå 💪" };
        context.Exercises.AddRange(bench, pullUp, unused);
        await context.SaveChangesAsync();

        var finished = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 3, 2),
            StartedAt = new DateTimeOffset(2026, 3, 1, 22, 30, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 3, 1, 23, 45, 12, TimeSpan.Zero),
            Title = "Aamutreeni – ääkköset",
            Location = "Salí Norte",
            Notes = "Line one\nLine \"two\" 🏋️",
            BodyweightKg = 81.35m,
        };
        var benchBlock = new WorkoutExercise { ExerciseId = bench.Id, Position = 0 };
        benchBlock.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = 60.00m, Reps = 10, IsWarmup = true });
        benchBlock.SetEntries.Add(new SetEntry { SetNumber = 2, Weight = 102.50m, Reps = 5 });
        var pullUpBlock = new WorkoutExercise { ExerciseId = pullUp.Id, Position = 1 };
        pullUpBlock.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = null, Reps = 8 });
        pullUpBlock.SetEntries.Add(new SetEntry { SetNumber = 2, Weight = 7.5m, Reps = 5 });
        var benchAgain = new WorkoutExercise { ExerciseId = bench.Id, Position = 2 };
        benchAgain.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = 80.25m, Reps = 8 });
        finished.WorkoutExercises.Add(benchBlock);
        finished.WorkoutExercises.Add(pullUpBlock);
        finished.WorkoutExercises.Add(benchAgain);

        var open = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 3, 5),
            StartedAt = new DateTimeOffset(2026, 3, 5, 7, 0, 0, TimeSpan.Zero),
        };
        var openBlock = new WorkoutExercise { ExerciseId = pullUp.Id, Position = 0 };
        openBlock.SetEntries.Add(new SetEntry { SetNumber = 1, Reps = 3 });
        open.WorkoutExercises.Add(openBlock);

        context.Workouts.AddRange(finished, open);
        await context.SaveChangesAsync();

        await db.ExecuteAsync(
            "UPDATE users SET acknowledged_privacy_notice_version = 'test-notice', privacy_notice_acknowledged_at = now() WHERE id = @id",
            userId);
        return userId;
    }

    // Account B: canary values in every free-text field, so any leak into A's file shows.
    private async Task SeedCanaryAccountAsync()
    {
        var userId = await db.SeedUserAsync();
        await using var context = db.NewContext();
        var exercise = new Exercise { UserId = userId, Name = $"{CanaryB} exercise", NormalizedName = $"{CanaryB.ToLowerInvariant()} exercise" };
        context.Exercises.Add(exercise);
        await context.SaveChangesAsync();
        var workout = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 3, 3),
            StartedAt = new DateTimeOffset(2026, 3, 3, 8, 0, 0, TimeSpan.Zero),
            Title = $"{CanaryB} title",
            Location = $"{CanaryB} location",
            Notes = $"{CanaryB} notes",
            BodyweightKg = 99.99m,
        };
        var block = new WorkoutExercise { ExerciseId = exercise.Id, Position = 0 };
        block.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = 999.99m, Reps = 99 });
        workout.WorkoutExercises.Add(block);
        context.Workouts.Add(workout);
        await context.SaveChangesAsync();
    }

    private static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture);

    private static DateTimeOffset? NullableInstant(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : Instant(element);

    private static string? NullableText(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    // Value and written form both: 102.50 stays "102.50", with no display rounding.
    private static void AssertDecimal(decimal? expected, JsonElement actual)
    {
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
            return;
        }
        Assert.Equal(expected.Value, actual.GetDecimal());
        Assert.Equal(expected.Value.ToString(CultureInfo.InvariantCulture), actual.GetRawText());
    }
}

// Shared by the export test classes.
internal static class ExportTestSupport
{
    public static Dictionary<string, string?> FlagOn(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["PRIVACY_LIFECYCLE_ENABLED"] = "true" };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }
        return settings;
    }

    public static Task<HttpResponseMessage> ExportAsync(
        HttpClient client, string password = TwoHostGymNotebookFixture.Password,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/export")
        {
            Content = JsonContent.Create(new { currentPassword = password }),
        };
        return client.SendAsync(request, completion);
    }
}
