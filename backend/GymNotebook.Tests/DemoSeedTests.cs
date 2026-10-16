using System.Text.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// specs/007 research R4: the demo notebook generator. Pure, so no database: each test runs
// over many seeds with "now" fixed, and checks what must hold whatever the draw.
public class DemoSeedTests
{
    // A Friday evening in Helsinki summer time.
    private static readonly DateTimeOffset _localNow = new(2026, 10, 16, 18, 30, 0, TimeSpan.FromHours(3));

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (var seed = 1; seed <= 50; seed++) data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_PassesRestoreValidation(int seed)
    {
        // Arrange
        var file = DemoSeed.Build(_localNow, new Random(seed));

        // Act
        var reason = NotebookRestore.Validate(file);

        // Assert
        Assert.Null(reason);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_HasAboutTwelveWeeksOfFinishedPagesEndingBeforeToday(int seed)
    {
        // Arrange
        var today = DateOnly.FromDateTime(_localNow.Date);

        // Act
        var finished = FinishedPages(DemoSeed.Build(_localNow, new Random(seed)));

        // Assert
        Assert.InRange(finished.Count, 30, 40);
        Assert.True(today.DayNumber - finished[0].Date!.Value.DayNumber >= 80, "the first page is at least 80 days back");
        Assert.InRange(today.DayNumber - finished[^1].Date!.Value.DayNumber, 1, 4);
        Assert.All(finished, w => Assert.True(w.StartedAt < _localNow));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_HasOneInProgressPageTodayWithDoneAndPlannedSets(int seed)
    {
        // Act
        var file = DemoSeed.Build(_localNow, new Random(seed));

        // Assert
        var open = Assert.Single(file.Workouts!, w => w.EndedAt is null);
        Assert.Equal(DateOnly.FromDateTime(_localNow.Date), open.Date);
        var statuses = SetsOf(file, open.Id!.Value).Select(s => s.Status).ToHashSet();
        Assert.Equal(["done", "planned"], statuses.Order());
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_HasThreeTemplatesWithSets(int seed)
    {
        // Act
        var file = DemoSeed.Build(_localNow, new Random(seed));

        // Assert
        Assert.Equal(3, file.Templates!.Count);
        Assert.All(file.Templates, template =>
        {
            var blocks = file.TemplateExercises!.Where(b => b.TemplateId == template.Id).Select(b => b.Id).ToHashSet();
            Assert.NotEmpty(blocks);
            Assert.All(blocks, block => Assert.Contains(file.TemplateSets!, s => s.TemplateExerciseId == block));
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_IncludesWarmupsSkippedSetsAndBodyweightExercises(int seed)
    {
        // Act
        var file = DemoSeed.Build(_localNow, new Random(seed));

        // Assert
        Assert.Contains(file.Sets!, s => s.IsWarmup == true);
        Assert.InRange(file.Sets!.Count(s => s.Status == "skipped"), 3, 4);
        foreach (var name in new[] { "Pull-up", "Dips" })
        {
            var exercise = Assert.Single(file.Exercises!, e => e.Name == name);
            Assert.True(exercise.IsBodyweight);
            var blocks = file.WorkoutExercises!.Where(b => b.ExerciseId == exercise.Id).Select(b => b.Id).ToHashSet();
            Assert.NotEmpty(blocks);
            Assert.All(file.Sets!.Where(s => blocks.Contains(s.WorkoutExerciseId)), s => Assert.Null(s.Weight));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_WeightsAreWholePlateSteps(int seed)
    {
        // Act
        var file = DemoSeed.Build(_localNow, new Random(seed));

        // Assert: pages and templates alike.
        var weights = file.Sets!.Select(s => s.Weight).Concat(file.TemplateSets!.Select(s => s.Weight)).OfType<decimal>().ToList();
        Assert.NotEmpty(weights);
        Assert.All(weights, w =>
        {
            Assert.Equal(0m, w % 2.5m);
            Assert.InRange(w, 20m, 300m);
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_MainLiftTrendsUpWithOneDeloadWeek(int seed)
    {
        // Arrange
        var file = DemoSeed.Build(_localNow, new Random(seed));
        var squat = file.Exercises!.Single(e => e.Name == "Back Squat").Id;

        // Act: a training week is three sessions; each week's best working squat.
        var weeklyBest = FinishedPages(file)
            .Chunk(3)
            .Select(week => week
                .SelectMany(w => file.WorkoutExercises!.Where(b => b.WorkoutId == w.Id && b.ExerciseId == squat))
                .SelectMany(b => file.Sets!.Where(s => s.WorkoutExerciseId == b.Id && s.IsWarmup == false))
                .Max(s => s.Weight!.Value))
            .ToList();

        // Assert
        Assert.True(weeklyBest[^1] > weeklyBest[0], $"last week {weeklyBest[^1]} above first week {weeklyBest[0]}");
        var dips = Enumerable.Range(1, weeklyBest.Count - 2)
            .Count(i => weeklyBest[i] < weeklyBest[i - 1] && weeklyBest[i] < weeklyBest[i + 1]);
        Assert.Equal(1, dips);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Build_AnySeed_SomePagesCarryOptionalDetails(int seed)
    {
        // Act
        var pages = DemoSeed.Build(_localNow, new Random(seed)).Workouts!;

        // Assert
        Assert.Contains(pages, w => w.Title is not null);
        Assert.Contains(pages, w => w.Location is not null);
        Assert.Contains(pages, w => w.Notes is not null);
        Assert.Contains(pages, w => w.BodyweightKg is not null);
    }

    [Fact]
    public void Build_SameSeed_IsDeterministic()
    {
        // Act
        var first = JsonSerializer.Serialize(DemoSeed.Build(_localNow, new Random(42)));
        var second = JsonSerializer.Serialize(DemoSeed.Build(_localNow, new Random(42)));

        // Assert
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 10)]
    [InlineData(0, 39)]
    [InlineData(5, 0)]
    public void Build_LocalNowEarlyMorning_StillPutsEveryPageInThePast(int hour, int minute)
    {
        // Arrange: just after midnight, when "40 minutes ago" would be yesterday. (At
        // exactly 00:00 no start can be both today and before now; the page then starts a
        // minute earlier, yesterday.)
        var now = new DateTimeOffset(2026, 10, 16, hour, minute, 0, TimeSpan.FromHours(-5));

        // Act
        var file = DemoSeed.Build(now, new Random(7));

        // Assert: valid, every start before now, and today's page still dated today.
        Assert.Null(NotebookRestore.Validate(file));
        Assert.All(file.Workouts!, w => Assert.True(w.StartedAt < now, $"{w.StartedAt} before {now}"));
        Assert.Equal(DateOnly.FromDateTime(now.Date), file.Workouts!.Single(w => w.EndedAt is null).Date);
    }

    private static List<RestoreWorkout> FinishedPages(RestoreFile file) =>
        file.Workouts!.Where(w => w.EndedAt is not null).OrderBy(w => w.StartedAt).ToList();

    private static IEnumerable<RestoreSet> SetsOf(RestoreFile file, int workoutId)
    {
        var blocks = file.WorkoutExercises!.Where(b => b.WorkoutId == workoutId).Select(b => b.Id).ToHashSet();
        return file.Sets!.Where(s => blocks.Contains(s.WorkoutExerciseId));
    }
}
