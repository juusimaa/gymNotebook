using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GymNotebook.Tests;

// The RemovePreparedWorkouts migration (specs/006 research R15). Prepared pages are not
// thrown away: each becomes a template with its blocks and sets, named by its title, or
// "Plan" and its UTC creation day, with a number when the account already uses the name.
// Then the page goes and date and started_at are required again.
[Collection("Lifecycle")]
public class RemovePreparedWorkoutsMigrationTests(TwoHostGymNotebookFixture db)
{
    private const string MigrationBefore = "20261015111821_AddTemplates";

    [Fact]
    public async Task Migrate_PreparedPages_BecomeTemplates()
    {
        // Arrange: a database one step before, with an account holding a template "push a",
        // a started page, and two prepared pages: one titled " Push  A " (a clash after
        // normalizing) with a warm-up and a working set, one untitled with a bodyweight set.
        var connectionString = await db.CreateEmptyDatabaseAsync();
        await using (var context = db.NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(MigrationBefore);
        }
        await ExecuteAsync(connectionString,
            """
            INSERT INTO users (display_name, email, password_hash, token_version, privacy_account_id)
            VALUES ('Lifter', 'lifter@example.test', 'hash', 0, gen_random_uuid());
            INSERT INTO exercises (user_id, name, normalized_name, is_bodyweight)
            VALUES (1, 'Bench Press', 'bench press', false), (1, 'Dips', 'dips', true);
            INSERT INTO templates (user_id, name, normalized_name) VALUES (1, 'push a', 'push a');

            INSERT INTO workouts (id, user_id, date, started_at, ended_at, title)
            VALUES (1, 1, DATE '2026-10-01', TIMESTAMPTZ '2026-10-01 07:00Z', TIMESTAMPTZ '2026-10-01 08:00Z', 'Logged');
            INSERT INTO workouts (id, user_id, title, created_at)
            VALUES (2, 1, ' Push  A ', TIMESTAMPTZ '2026-10-10 18:00Z'),
                   (3, 1, NULL, TIMESTAMPTZ '2026-10-14 23:30Z');

            INSERT INTO workout_exercises (id, workout_id, exercise_id, position)
            VALUES (1, 1, 1, 0), (2, 2, 1, 0), (3, 3, 2, 0);
            INSERT INTO set_entries (workout_exercise_id, set_number, weight, reps, is_warmup, status)
            VALUES (1, 1, 100, 5, false, 'done'),
                   (2, 1, 60, 8, true, 'planned'), (2, 2, 85, 5, false, 'planned'),
                   (3, 1, NULL, 10, false, 'planned');
            """);

        // Act
        await using var migrated = db.NewContext(connectionString);
        await migrated.Database.MigrateAsync();

        // Assert: the started page alone is left, with its set.
        var page = Assert.Single(await migrated.Workouts.ToListAsync());
        Assert.Equal((1, "Logged"), (page.Id, page.Title));
        Assert.Equal(1, await migrated.SetEntries.CountAsync());

        // The account's template is untouched; the plans follow it, by creation time.
        var templates = await migrated.Templates.Include(t => t.Exercises).ThenInclude(e => e.Sets)
            .OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(
            [("push a", "push a"), ("Push  A (2)", "push a (2)"), ("Plan 2026-10-14", "plan 2026-10-14")],
            templates.Select(t => (t.Name, t.NormalizedName)));
        Assert.Empty(templates[0].Exercises);

        var bench = Assert.Single(templates[1].Exercises);
        Assert.Equal((1, 0), (bench.ExerciseId, bench.Position));
        Assert.Equal(
            [(1, 60m, 8, true), (2, 85m, 5, false)],
            bench.Sets.OrderBy(s => s.SetNumber).Select(s => (s.SetNumber, s.Weight!.Value, s.Reps, s.IsWarmup)));

        var dips = Assert.Single(templates[2].Exercises);
        var dipSet = Assert.Single(dips.Sets);
        Assert.Equal((2, (decimal?)null, 10), (dips.ExerciseId, dipSet.Weight, dipSet.Reps));
    }

    [Fact]
    public async Task Migrate_AfterwardsPageWithoutStart_IsRefused()
    {
        // Arrange
        var connectionString = await db.CreateEmptyDatabaseAsync();
        await using (var context = db.NewContext(connectionString))
        {
            await context.Database.MigrateAsync();
        }
        await ExecuteAsync(connectionString,
            "INSERT INTO users (display_name, email, password_hash, token_version, privacy_account_id) VALUES ('Lifter', 'lifter@example.test', 'hash', 0, gen_random_uuid())");

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteAsync(connectionString, "INSERT INTO workouts (user_id) VALUES (1)"));

        // Assert: date and started_at are required again (23502 not_null_violation).
        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.Equal(PostgresErrorCodes.NotNullViolation, postgres.SqlState);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
