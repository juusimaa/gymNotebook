namespace GymNotebook.Api;

// The demo notebook (specs/007 research R4): about twelve weeks of believable training,
// built as a format-2 RestoreFile so it goes in through restore's own import
// (NotebookRestore.ImportAsync, R5) under the same rules as a backup.
//
// A pure function: no clock, no database, no shared Random. The caller passes the
// visitor's local "now" (server UTC shifted by the browser's offset, so "today" and "late
// afternoon" mean the visitor's day) and the Random to draw from. Production passes
// `new Random()`, so demos differ slightly; tests pass fixed seeds and check invariants.
public static class DemoSeed
{
    // Three sessions a week, Upper and Lower alternating: twelve training weeks of
    // finished pages, plus today's page in progress.
    private const int TrainingWeeks = 12;
    private const int SessionsPerWeek = 3;
    private static readonly DayOfWeek[] _trainingDays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];

    // Week 8 (index 7) is a deload at about 85 %, so the e1RM chart dips visibly once
    // before climbing again — what a real programme's chart looks like.
    private const int DeloadWeek = 7;
    private const decimal DeloadFactor = 0.85m;

    // The smallest jump a pair of change plates allows; every weight is a multiple of it.
    private const decimal PlateStep = 2.5m;

    // An empty Olympic bar: the first warm-up of the day's first barbell lift.
    private const decimal EmptyBar = 20m;

    // Sessions start in the late afternoon, give or take 40 minutes, and last 55–80
    // minutes: a typical after-work gym visit.
    private static readonly TimeSpan _usualStart = new(17, 0, 0);
    private const int StartJitterMinutes = 40;
    private const int MinDurationMinutes = 55;
    private const int MaxDurationMinutes = 80;

    // Today's page was started from the Lower template about this long before "now".
    private const int TodayStartedMinutesAgo = 40;

    // How a weighted lift is trained: where it starts, how much it gains a week
    // (rounded down to whole plates, so a 1.25 kg step moves every other week), and
    // its sets × reps. 3×5 for the heavy compounds, 3×8–10 for the rest.
    private sealed record Lift(string Name, decimal StartKg, decimal WeeklyStepKg, int Sets, int Reps);

    // Bodyweight exercises progress by reps instead: Pull-ups from about 6 to 10,
    // Dips from about 8 to 14.
    private sealed record BodyweightMove(string Name, int StartReps, int EndReps, int Sets);

    private static readonly Lift _backSquat = new("Back Squat", 80m, 2.5m, 3, 5);
    private static readonly Lift _benchPress = new("Bench Press", 60m, 2.5m, 3, 5);
    // The deadlift moves fastest, as it does for most beginners.
    private static readonly Lift _deadlift = new("Deadlift", 100m, 5m, 1, 5);
    private static readonly Lift _overheadPress = new("Overhead Press", 40m, 1.25m, 3, 8);
    private static readonly Lift _barbellRow = new("Barbell Row", 60m, 2.5m, 3, 8);
    private static readonly Lift _romanianDeadlift = new("Romanian Deadlift", 80m, 2.5m, 3, 8);
    private static readonly Lift _latPulldown = new("Lat Pulldown", 50m, 1.25m, 3, 10);
    // Per dumbbell, as people write it.
    private static readonly Lift _inclineDumbbellPress = new("Incline Dumbbell Press", 22.5m, 1.25m, 3, 10);
    private static readonly BodyweightMove _pullUp = new("Pull-up", 6, 10, 3);
    private static readonly BodyweightMove _dips = new("Dips", 8, 14, 3);

    // Only in the "Arms & Core" template, so the template list isn't just a mirror of
    // the rotation, and the exercise index shows a few not logged yet.
    private static readonly Lift _barbellCurl = new("Barbell Curl", 30m, 0m, 3, 10);
    private static readonly Lift _tricepsPushdown = new("Triceps Pushdown", 25m, 0m, 3, 12);
    private static readonly BodyweightMove _hangingLegRaise = new("Hanging Leg Raise", 10, 10, 3);

    // The optional details (specs/001 FR-029). Made up, so they can't point at anyone.
    private static readonly string[] _locations = ["Home gym", "City gym"];
    private static readonly string[] _notes =
    [
        "Bench moved fast today.",
        "Slept badly, kept it honest.",
        "Grip gave out on the last row set.",
        "Squat depth felt better with the new shoes.",
        "Short on time, kept the rests tight.",
        "Lower back a bit tight, went easy on the RDLs.",
        "Felt strong, could have added weight.",
    ];
    private const decimal StartBodyweightKg = 82m;

    public static RestoreFile Build(DateTimeOffset localNow, Random random)
    {
        var builder = new Builder(localNow, random);
        builder.AddExercises();
        builder.AddFinishedPages();
        builder.AddTodaysPage();
        builder.AddTemplates();
        return builder.File;
    }

    private sealed class Builder(DateTimeOffset localNow, Random random)
    {
        public RestoreFile File { get; } = new()
        {
            FormatVersion = 2,
            Exercises = [],
            Workouts = [],
            WorkoutExercises = [],
            Sets = [],
            Templates = [],
            TemplateExercises = [],
            TemplateSets = [],
        };

        private readonly Dictionary<string, int> _exerciseIds = [];

        // The weight each lift was last done with, for the "repeat last time" noise.
        private readonly Dictionary<string, decimal> _lastWeight = [];

        public void AddExercises()
        {
            Lift[] weighted = [_backSquat, _benchPress, _deadlift, _overheadPress, _barbellRow,
                _romanianDeadlift, _latPulldown, _inclineDumbbellPress, _barbellCurl, _tricepsPushdown];
            BodyweightMove[] bodyweight = [_pullUp, _dips, _hangingLegRaise];
            foreach (var lift in weighted) AddExercise(lift.Name, isBodyweight: false);
            foreach (var move in bodyweight) AddExercise(move.Name, isBodyweight: true);
        }

        private void AddExercise(string name, bool isBodyweight)
        {
            var id = File.Exercises!.Count + 1;
            File.Exercises.Add(new RestoreExercise { Id = id, Name = name, IsBodyweight = isBodyweight });
            _exerciseIds.Add(name, id);
        }

        public void AddFinishedPages()
        {
            var days = TrainingDaysBeforeToday();
            // 3–4 finished pages end with their last accessory set skipped: the kind of
            // set people plan and then don't do.
            var skippedPages = Enumerable.Range(0, days.Count).OrderBy(_ => random.Next()).Take(random.Next(3, 5)).ToHashSet();
            var bodyweight = StartBodyweightKg;

            for (var session = 0; session < days.Count; session++)
            {
                var week = session / SessionsPerWeek;
                var upper = session % 2 == 0;
                var start = new DateTimeOffset(days[session].ToDateTime(TimeOnly.MinValue), localNow.Offset)
                    + _usualStart + TimeSpan.FromMinutes(random.Next(-StartJitterMinutes, StartJitterMinutes + 1));
                var end = start + TimeSpan.FromMinutes(random.Next(MinDurationMinutes, MaxDurationMinutes + 1));

                // Bodyweight drifts slowly, ±0.3 kg a session around a slight upward trend.
                bodyweight = Math.Round(bodyweight + (random.Next(-3, 5) / 10m), 1);
                var isLast = session == days.Count - 1;
                var workout = new RestoreWorkout
                {
                    Id = File.Workouts!.Count + 1,
                    Date = days[session],
                    StartedAt = start,
                    EndedAt = end,
                    // A title on most pages, a location on some, notes on about one in five
                    // and bodyweight on about one in three. The last page carries all four,
                    // so every kind of detail is on show whatever the draw.
                    Title = isLast || random.Next(8) != 0 ? (upper ? "Upper" : "Lower") : null,
                    Location = isLast || random.Next(5) < 2 ? _locations[random.Next(_locations.Length)] : null,
                    Notes = isLast || random.Next(5) == 0 ? _notes[random.Next(_notes.Length)] : null,
                    BodyweightKg = isLast || random.Next(3) == 0 ? bodyweight : null,
                };
                File.Workouts.Add(workout);

                var blocks = upper ? UpperDay(session, week) : LowerDay(session, week);
                for (var position = 0; position < blocks.Count; position++)
                {
                    var (exercise, sets) = blocks[position];
                    var block = AddBlock(workout.Id.Value, exercise, position);
                    var skipLast = position == blocks.Count - 1 && skippedPages.Contains(session);
                    for (var i = 0; i < sets.Count; i++)
                    {
                        var status = skipLast && i == sets.Count - 1 ? SetStatus.Skipped : SetStatus.Done;
                        AddSet(block, i + 1, sets[i], status);
                    }
                }
            }
        }

        // Started from "Lower" about 40 minutes ago and not finished: the squats are done,
        // the rest still planned — what a visitor would see mid-session.
        public void AddTodaysPage()
        {
            var midnight = new DateTimeOffset(localNow.Date, localNow.Offset);
            var start = localNow - TimeSpan.FromMinutes(TodayStartedMinutesAgo);
            // Just after midnight, the page still belongs to today, and still started
            // before "now" (a minute is enough when it's exactly midnight).
            if (start < midnight) start = localNow - midnight >= TimeSpan.FromMinutes(1) ? midnight : localNow - TimeSpan.FromMinutes(1);

            var workout = new RestoreWorkout
            {
                Id = File.Workouts!.Count + 1,
                Date = DateOnly.FromDateTime(start.DateTime),
                StartedAt = start,
                Title = "Lower",
            };
            File.Workouts.Add(workout);

            var blocks = LowerTemplate();
            for (var position = 0; position < blocks.Count; position++)
            {
                var (exercise, sets) = blocks[position];
                var block = AddBlock(workout.Id.Value, exercise, position);
                for (var i = 0; i < sets.Count; i++)
                {
                    AddSet(block, i + 1, sets[i], position == 0 ? SetStatus.Done : SetStatus.Planned);
                }
            }
        }

        public void AddTemplates()
        {
            AddTemplate("Upper", UpperTemplate());
            AddTemplate("Lower", LowerTemplate());
            AddTemplate("Arms & Core",
            [
                (_barbellCurl.Name, Working(_barbellCurl, _barbellCurl.StartKg)),
                (_tricepsPushdown.Name, Working(_tricepsPushdown, _tricepsPushdown.StartKg)),
                (_hangingLegRaise.Name, Bodyweight(_hangingLegRaise, _hangingLegRaise.StartReps)),
            ]);
        }

        // The latest Monday, Wednesday or Friday before today, and the 35 training days
        // before it, oldest first. Never today: today has its own page.
        private List<DateOnly> TrainingDaysBeforeToday()
        {
            var days = new List<DateOnly>();
            var day = DateOnly.FromDateTime(localNow.Date).AddDays(-1);
            while (days.Count < TrainingWeeks * SessionsPerWeek)
            {
                if (_trainingDays.Contains(day.DayOfWeek)) days.Add(day);
                day = day.AddDays(-1);
            }
            days.Reverse();
            return days;
        }

        // Upper: bench first (with warm-ups), then press, row and pull-ups, and an
        // accessory that alternates between incline presses and dips.
        private List<(string, List<PlannedSet>)> UpperDay(int session, int week) =>
        [
            (_benchPress.Name, WithWarmups(Working(_benchPress, SessionWeight(_benchPress, week)))),
            (_overheadPress.Name, Working(_overheadPress, SessionWeight(_overheadPress, week))),
            (_barbellRow.Name, Working(_barbellRow, SessionWeight(_barbellRow, week))),
            (_pullUp.Name, Bodyweight(_pullUp, Reps(_pullUp, week))),
            session % 4 == 0
                ? (_inclineDumbbellPress.Name, Working(_inclineDumbbellPress, SessionWeight(_inclineDumbbellPress, week)))
                : (_dips.Name, Bodyweight(_dips, Reps(_dips, week))),
        ];

        // Lower: squats first (with warm-ups), then deadlifts or Romanian deadlifts on
        // alternate Lower days, and pulldowns for the upper back.
        private List<(string, List<PlannedSet>)> LowerDay(int session, int week) =>
        [
            (_backSquat.Name, WithWarmups(Working(_backSquat, SessionWeight(_backSquat, week)))),
            session % 4 == 1
                ? (_deadlift.Name, Working(_deadlift, SessionWeight(_deadlift, week)))
                : (_romanianDeadlift.Name, Working(_romanianDeadlift, SessionWeight(_romanianDeadlift, week))),
            (_latPulldown.Name, Working(_latPulldown, SessionWeight(_latPulldown, week))),
        ];

        // The templates hold next week's weights: what the visitor would lift next.
        private static List<(string, List<PlannedSet>)> UpperTemplate() =>
        [
            (_benchPress.Name, WithWarmups(Plain(_benchPress, PlannedWeight(_benchPress, TrainingWeeks)))),
            (_overheadPress.Name, Plain(_overheadPress, PlannedWeight(_overheadPress, TrainingWeeks))),
            (_barbellRow.Name, Plain(_barbellRow, PlannedWeight(_barbellRow, TrainingWeeks))),
            (_pullUp.Name, Bodyweight(_pullUp, _pullUp.EndReps)),
            (_inclineDumbbellPress.Name, Plain(_inclineDumbbellPress, PlannedWeight(_inclineDumbbellPress, TrainingWeeks))),
        ];

        private static List<(string, List<PlannedSet>)> LowerTemplate() =>
        [
            (_backSquat.Name, WithWarmups(Plain(_backSquat, PlannedWeight(_backSquat, TrainingWeeks)))),
            (_romanianDeadlift.Name, Plain(_romanianDeadlift, PlannedWeight(_romanianDeadlift, TrainingWeeks))),
            (_latPulldown.Name, Plain(_latPulldown, PlannedWeight(_latPulldown, TrainingWeeks))),
        ];

        // This session's weight for a lift. About one session in six repeats last time's
        // weight instead of moving up: a bad day, or a weight that wasn't owned yet. Never
        // in the deload week or the week after, so the deload stays the chart's one dip.
        private decimal SessionWeight(Lift lift, int week)
        {
            var weight = PlannedWeight(lift, week);
            var noiseAllowed = week != DeloadWeek && week != DeloadWeek + 1;
            if (noiseAllowed && random.Next(6) == 0 && _lastWeight.TryGetValue(lift.Name, out var last))
            {
                weight = last;
            }
            _lastWeight[lift.Name] = weight;
            return weight;
        }

        private static decimal PlannedWeight(Lift lift, int week)
        {
            var weight = lift.StartKg + (lift.WeeklyStepKg * week);
            if (week == DeloadWeek) weight *= DeloadFactor;
            return ToPlates(weight);
        }

        // Reps climb evenly from the start to the end figure over the twelve weeks; the
        // deload week does two fewer.
        private static int Reps(BodyweightMove move, int week)
        {
            var reps = move.StartReps + (int)Math.Round((move.EndReps - move.StartReps) * week / (double)(TrainingWeeks - 1));
            return week == DeloadWeek ? Math.Max(1, reps - 2) : reps;
        }

        // Working sets; about one time in eight the last set comes up a rep short.
        private List<PlannedSet> Working(Lift lift, decimal weight)
        {
            var sets = Plain(lift, weight);
            if (random.Next(8) == 0) sets[^1] = sets[^1] with { Reps = sets[^1].Reps - 1 };
            return sets;
        }

        private static List<PlannedSet> Plain(Lift lift, decimal weight) =>
            Enumerable.Range(0, lift.Sets).Select(_ => new PlannedSet(weight, lift.Reps, IsWarmup: false)).ToList();

        private static List<PlannedSet> Bodyweight(BodyweightMove move, int reps) =>
            Enumerable.Range(0, move.Sets).Select(_ => new PlannedSet(null, reps, IsWarmup: false)).ToList();

        // Two warm-ups before the day's first barbell lift: the empty bar for ten, then
        // about 60 % of the working weight for five.
        private static List<PlannedSet> WithWarmups(List<PlannedSet> working) =>
        [
            new PlannedSet(EmptyBar, 10, IsWarmup: true),
            new PlannedSet(Math.Max(EmptyBar, ToPlates(working[0].Weight!.Value * 0.6m)), 5, IsWarmup: true),
            .. working,
        ];

        private static decimal ToPlates(decimal kg) => Math.Floor(kg / PlateStep) * PlateStep;

        private int AddBlock(int workoutId, string exercise, int position)
        {
            var id = File.WorkoutExercises!.Count + 1;
            File.WorkoutExercises.Add(new RestoreBlock { Id = id, WorkoutId = workoutId, ExerciseId = _exerciseIds[exercise], Position = position });
            return id;
        }

        private void AddSet(int blockId, int setNumber, PlannedSet set, SetStatus status) =>
            File.Sets!.Add(new RestoreSet
            {
                Id = File.Sets.Count + 1,
                WorkoutExerciseId = blockId,
                SetNumber = setNumber,
                Weight = set.Weight,
                Reps = set.Reps,
                IsWarmup = set.IsWarmup,
                Status = SetStatusRules.ToText(status),
            });

        private void AddTemplate(string name, List<(string Exercise, List<PlannedSet> Sets)> blocks)
        {
            var templateId = File.Templates!.Count + 1;
            File.Templates.Add(new RestoreTemplate { Id = templateId, Name = name });
            for (var position = 0; position < blocks.Count; position++)
            {
                var blockId = File.TemplateExercises!.Count + 1;
                File.TemplateExercises.Add(new RestoreTemplateBlock
                {
                    Id = blockId,
                    TemplateId = templateId,
                    ExerciseId = _exerciseIds[blocks[position].Exercise],
                    Position = position,
                });
                var sets = blocks[position].Sets;
                for (var i = 0; i < sets.Count; i++)
                {
                    File.TemplateSets!.Add(new RestoreTemplateSet
                    {
                        Id = File.TemplateSets.Count + 1,
                        TemplateExerciseId = blockId,
                        SetNumber = i + 1,
                        Weight = sets[i].Weight,
                        Reps = sets[i].Reps,
                        IsWarmup = sets[i].IsWarmup,
                    });
                }
            }
        }
    }

    private sealed record PlannedSet(decimal? Weight, int Reps, bool IsWarmup);
}
