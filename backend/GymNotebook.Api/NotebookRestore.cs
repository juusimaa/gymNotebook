using System.Text.Json;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace GymNotebook.Api;

public sealed class RestoreOptions
{
    public long MaxBytes { get; set; } = 26_214_400;

    public void Validate()
    {
        if (MaxBytes <= 0) throw new InvalidOperationException("Restore:MaxBytes must be positive.");
    }
}

public sealed record RestoreResponse(
    int PagesAdded, int SetsAdded, int PagesAlreadyPresent,
    string[] ExercisesCreated, string[] ClassificationKept, int OptionalDetailsDropped);

public sealed record RestoreError(string Code, string Reason);

// Only the columns that can become notebook rows are deserialized. File account, consent,
// privacy records, creation times and user IDs are deliberately not represented here.
public sealed class RestoreFile
{
    public int? FormatVersion { get; set; }
    public List<RestoreExercise>? Exercises { get; set; }
    public List<RestoreWorkout>? Workouts { get; set; }
    public List<RestoreBlock>? WorkoutExercises { get; set; }
    public List<RestoreSet>? Sets { get; set; }
}

public sealed class RestoreExercise
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public bool? IsBodyweight { get; set; }
}

public sealed class RestoreWorkout
{
    public int? Id { get; set; }
    public DateOnly? Date { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Title { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public decimal? BodyweightKg { get; set; }
}

public sealed class RestoreBlock
{
    public int? Id { get; set; }
    public int? WorkoutId { get; set; }
    public int? ExerciseId { get; set; }
    public int? Position { get; set; }
}

public sealed class RestoreSet
{
    public int? Id { get; set; }
    public int? WorkoutExerciseId { get; set; }
    public int? SetNumber { get; set; }
    public decimal? Weight { get; set; }
    public int? Reps { get; set; }
    public bool? IsWarmup { get; set; }
}

public sealed class NotebookRestore(AppDbContext db, RestoreOptions options)
{
    public const int RestoreLockNamespace = 0x52535452; // RSTR, separate from lifecycle/export.
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IResult> RunAsync(HttpContext http, bool privacyLifecycleEnabled)
    {
        if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Request.ContentLength > options.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // TestServer does not implement Kestrel's request-size feature. This bounded copy
        // enforces the same cap for chunked requests without retaining an oversized file.
        using var body = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted);
            if (read == 0) break;
            if (body.Length + read > options.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            body.Write(buffer, 0, read);
        }
        body.Position = 0;

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 8 }, http.RequestAborted);
        }
        catch (JsonException)
        {
            return Invalid("not_a_backup");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var version) ||
                !HasArray(root, "exercises") || !HasArray(root, "workouts") ||
                !HasArray(root, "workoutExercises") || !HasArray(root, "sets"))
                return Invalid("not_a_backup");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
                return Invalid("unsupported_version");
            RestoreFile? file;
            try
            {
                file = root.Deserialize<RestoreFile>(_jsonOptions);
            }
            catch (JsonException)
            {
                return Invalid("invalid_value");
            }

            var reason = Validate(file);
            if (reason is not null) return Invalid(reason);
            try
            {
                return await RestoreAsync(file!, http, privacyLifecycleEnabled);
            }
            catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested && AccountLifecycle.IsDatabaseFailure(ex))
            {
                // The lifecycle filter rolls back any writes on this non-2xx result.
                return AccountLifecycle.TemporarilyUnavailable(http);
            }
        }
    }

    public static string? Validate(RestoreFile? file)
    {
        if (file?.FormatVersion is null || file.Exercises is null || file.Workouts is null ||
            file.WorkoutExercises is null || file.Sets is null) return "not_a_backup";
        if (file.FormatVersion != 1) return "unsupported_version";

        var exerciseIds = new HashSet<int>();
        var workoutIds = new HashSet<int>();
        var blockIds = new HashSet<int>();
        var setIds = new HashSet<int>();
        var starts = new HashSet<DateTimeOffset>();
        var positions = new HashSet<(int WorkoutId, int Position)>();
        var setNumbers = new HashSet<(int BlockId, int SetNumber)>();

        foreach (var exercise in file.Exercises)
        {
            if (exercise is null || exercise.Id is not > 0 ||
                !exerciseIds.Add(exercise.Id.Value) ||
                !WorkoutInputValidation.HasExerciseName(exercise.Name) || exercise.IsBodyweight is null)
                return "invalid_value";
        }
        foreach (var workout in file.Workouts)
        {
            if (workout is null || workout.Id is not > 0 || !workoutIds.Add(workout.Id.Value) ||
                workout.Date is null || workout.StartedAt is null ||
                !starts.Add(workout.StartedAt.Value.ToUniversalTime()) ||
                (workout.EndedAt is not null && workout.EndedAt < workout.StartedAt) ||
                (workout.BodyweightKg is < 0 or > 999.99m) ||
                (workout.BodyweightKg is not null && decimal.Round(workout.BodyweightKg.Value, 2) != workout.BodyweightKg))
                return "invalid_value";
        }
        foreach (var block in file.WorkoutExercises)
        {
            if (block is null || block.Id is not > 0 || !blockIds.Add(block.Id.Value) ||
                block.WorkoutId is not > 0 || block.ExerciseId is not > 0 || block.Position is < 0 or null ||
                !positions.Add((block.WorkoutId.Value, block.Position.Value))) return "invalid_value";
            if (!workoutIds.Contains(block.WorkoutId.Value) || !exerciseIds.Contains(block.ExerciseId.Value))
                return "broken_reference";
        }
        foreach (var set in file.Sets)
        {
            if (set is null || set.Id is not > 0 || !setIds.Add(set.Id.Value) || set.WorkoutExerciseId is not > 0 ||
                set.SetNumber is not > 0 || set.Reps is not > 0 || set.IsWarmup is null ||
                set.Weight is < 0 or > 9999.99m ||
                (set.Weight is not null && decimal.Round(set.Weight.Value, 2) != set.Weight) ||
                !setNumbers.Add((set.WorkoutExerciseId.Value, set.SetNumber.Value))) return "invalid_value";
            if (!blockIds.Contains(set.WorkoutExerciseId.Value)) return "broken_reference";
        }
        return null;
    }

    private async Task<IResult> RestoreAsync(RestoreFile file, HttpContext http, bool privacyLifecycleEnabled)
    {
        var (userId, _) = AccountLifecycle.ReadClaims(http.User);
        var ct = http.RequestAborted;
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Restore requires the lifecycle transaction.");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@ns, @id)", connection,
            (NpgsqlTransaction)transaction.GetDbTransaction()))
        {
            command.Parameters.AddWithValue("ns", RestoreLockNamespace);
            command.Parameters.AddWithValue("id", userId);
            if (!((bool?)await command.ExecuteScalarAsync(ct) ?? false))
            {
                http.Response.Headers.RetryAfter = "5";
                return Results.Json(new ErrorResponse("restore_in_progress"), statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = 120000", ct);

        var fileTimes = file.Workouts!.Select(w => w.StartedAt!.Value.ToUniversalTime()).Distinct().ToArray();
        var existingTimes = fileTimes.Length == 0 ? [] : await db.Workouts.AsNoTracking()
            .Where(w => w.UserId == userId && fileTimes.Contains(w.StartedAt))
            .Select(w => w.StartedAt).ToArrayAsync(ct);
        var present = existingTimes.ToHashSet();
        var addedPages = file.Workouts!.Where(w => !present.Contains(w.StartedAt!.Value.ToUniversalTime())).ToList();
        var skipped = file.Workouts!.Count - addedPages.Count;
        var addedIds = addedPages.Select(w => w.Id!.Value).ToHashSet();
        var addedBlocks = file.WorkoutExercises!.Where(b => addedIds.Contains(b.WorkoutId!.Value)).ToList();
        var addedBlockIds = addedBlocks.Select(b => b.Id!.Value).ToHashSet();
        var addedSets = file.Sets!.Where(s => addedBlockIds.Contains(s.WorkoutExerciseId!.Value)).ToList();
        var usedExerciseIds = addedBlocks.Select(b => b.ExerciseId!.Value).ToHashSet();

        var names = file.Exercises!.Where(e => usedExerciseIds.Contains(e.Id!.Value))
            .Select(e => ExerciseNameNormalizer.Normalize(e.Name!)).Distinct().ToArray();
        var existingExercises = await db.Exercises.AsNoTracking()
            .Where(e => e.UserId == userId && names.Contains(e.NormalizedName))
            .Select(e => new { e.Id, e.NormalizedName, e.IsBodyweight }).ToListAsync(ct);
        var exerciseByName = existingExercises.ToDictionary(e => e.NormalizedName, e => e.Id);
        var classificationKept = new HashSet<string>();
        var created = new List<Exercise>();
        foreach (var source in file.Exercises!.Where(e => usedExerciseIds.Contains(e.Id!.Value)))
        {
            var normalized = ExerciseNameNormalizer.Normalize(source.Name!);
            if (exerciseByName.ContainsKey(normalized))
            {
                if (existingExercises.Any(e => e.NormalizedName == normalized && e.IsBodyweight != source.IsBodyweight))
                    classificationKept.Add(source.Name!);
                continue;
            }
            if (created.Any(e => e.NormalizedName == normalized)) continue;
            created.Add(new Exercise { UserId = userId, Name = source.Name!, NormalizedName = normalized, IsBodyweight = source.IsBodyweight!.Value });
        }
        db.Exercises.AddRange(created);
        await db.SaveChangesAsync(ct);
        foreach (var exercise in created) exerciseByName.Add(exercise.NormalizedName, exercise.Id);

        // Lock the consent row once for the whole restore. Withdrawal must wait for this
        // transaction, then clears imported details; if it won first we drop them here.
        var firstDetail = addedPages.FirstOrDefault(w => w.Title is not null || w.Location is not null ||
            w.Notes is not null || w.BodyweightKg is not null);
        var detailDecision = firstDetail is null ? OptionalDetailsDecision.Store :
            await OptionalDetails.DecideAsync(db, userId, privacyLifecycleEnabled,
                firstDetail.Title, firstDetail.BodyweightKg, firstDetail.Location, firstDetail.Notes, ct);
        var dropDetails = privacyLifecycleEnabled && detailDecision != OptionalDetailsDecision.Store;
        var dropped = dropDetails ? addedPages.Count(w => w.Title is not null || w.Location is not null ||
            w.Notes is not null || w.BodyweightKg is not null) : 0;

        var workoutByFileId = new Dictionary<int, int>();
        var workouts = addedPages.Select(source => new Workout
        {
            UserId = userId,
            Date = source.Date!.Value,
            StartedAt = source.StartedAt!.Value.ToUniversalTime(),
            EndedAt = source.EndedAt?.ToUniversalTime(),
            Title = dropDetails ? null : source.Title,
            Location = dropDetails ? null : source.Location,
            Notes = dropDetails ? null : source.Notes,
            BodyweightKg = dropDetails ? null : source.BodyweightKg,
        }).ToList();
        db.Workouts.AddRange(workouts);
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < workouts.Count; i++) workoutByFileId.Add(addedPages[i].Id!.Value, workouts[i].Id);

        var sourceExercises = file.Exercises!.ToDictionary(e => e.Id!.Value);
        var blocks = addedBlocks.Select(source => new WorkoutExercise
        {
            WorkoutId = workoutByFileId[source.WorkoutId!.Value],
            ExerciseId = exerciseByName[ExerciseNameNormalizer.Normalize(sourceExercises[source.ExerciseId!.Value].Name!)],
            Position = source.Position!.Value,
        }).ToList();
        db.WorkoutExercises.AddRange(blocks);
        await db.SaveChangesAsync(ct);
        var blockByFileId = new Dictionary<int, int>();
        for (var i = 0; i < blocks.Count; i++) blockByFileId.Add(addedBlocks[i].Id!.Value, blocks[i].Id);

        db.SetEntries.AddRange(addedSets.Select(source => new SetEntry
        {
            WorkoutExerciseId = blockByFileId[source.WorkoutExerciseId!.Value],
            SetNumber = source.SetNumber!.Value,
            Weight = source.Weight,
            Reps = source.Reps!.Value,
            IsWarmup = source.IsWarmup!.Value,
        }));
        await db.SaveChangesAsync(ct);
        return Results.Ok(new RestoreResponse(workouts.Count, addedSets.Count, skipped,
            created.Select(e => e.Name).ToArray(), classificationKept.ToArray(), dropped));
    }

    private static IResult Invalid(string reason) =>
        Results.Json(new RestoreError("backup_invalid", reason), statusCode: StatusCodes.Status400BadRequest);

    private static bool HasArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;
}
