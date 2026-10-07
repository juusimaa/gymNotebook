using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymNotebook.Tests;

// specs/001 user story 6 (tasks.md T097, quickstart §6a step 7): the operator's transition
// clearing SQL, exactly as docs/privacy/retention.md prints it, run against real
// PostgreSQL. The blocks are read from the document between their markers rather than
// copied here, so the SQL the operator pastes into the Neon console on the deadline is the
// SQL this test proved.
[Collection("Lifecycle")]
public class TransitionClearingSqlTests(TwoHostGymNotebookFixture db)
{
    [Fact]
    public async Task TransitionClearing_MixedAccounts_ClearsOnlyUnconsentedDetailsAndCountsThem()
    {
        // Arrange: a database of its own, so the counts cover only the accounts seeded here.
        var connectionString = await db.CreateEmptyDatabaseAsync();
        await using (var migrate = db.NewContext(connectionString))
        {
            await migrate.Database.MigrateAsync();
        }
        var pendingA = await SeedAccountAsync(connectionString, consent: false, detailWorkouts: 2, plainWorkouts: 1);
        var pendingB = await SeedAccountAsync(connectionString, consent: false, detailWorkouts: 1, plainWorkouts: 0);
        var consenting = await SeedAccountAsync(connectionString, consent: true, detailWorkouts: 2, plainWorkouts: 0);
        await SeedAccountAsync(connectionString, consent: false, detailWorkouts: 0, plainWorkouts: 2);
        var (clearSql, verifySql) = ReadDocumentedSql();

        // Act
        var (accounts, workouts) = await RunClearingAsync(connectionString, clearSql);
        var remaining = await ScalarAsync(connectionString, verifySql);

        // Assert: two accounts and three workouts cleared, and nothing left to clear.
        Assert.Equal(2, accounts);
        Assert.Equal(3, workouts);
        Assert.Equal(0, remaining);

        await using var context = db.NewContext(connectionString);
        var all = await context.Workouts.AsNoTracking().ToListAsync();
        Assert.All(all.Where(w => w.UserId == pendingA || w.UserId == pendingB), w =>
        {
            Assert.Null(w.Title);
            Assert.Null(w.Location);
            Assert.Null(w.Notes);
            Assert.Null(w.BodyweightKg);
        });
        // The consenting account keeps every detail; nothing else on any workout changed.
        Assert.All(all.Where(w => w.UserId == consenting), w => Assert.Equal(OptionalDetailsTestData.Notes, w.Notes));
        Assert.All(all, w => Assert.Equal(new DateOnly(2026, 3, 1), w.Date));

        // Running it again is harmless: zero of each.
        Assert.Equal((0, 0), await RunClearingAsync(connectionString, clearSql));
    }

    private async Task<int> SeedAccountAsync(string connectionString, bool consent, int detailWorkouts, int plainWorkouts)
    {
        await using var context = db.NewContext(connectionString);
        var user = new User
        {
            DisplayName = $"transition-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            // Confirmed: a token for an unconfirmed account is rejected (specs/002 FR-004).
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = "not-a-real-hash",
            PrivacyAccountId = Guid.NewGuid(),
            OptionalDetailsConsentVersion = consent ? "test-consent" : null,
            OptionalDetailsConsentedAt = consent ? DateTimeOffset.UtcNow : null,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        for (var i = 0; i < detailWorkouts + plainWorkouts; i++)
        {
            var withDetails = i < detailWorkouts;
            context.Workouts.Add(new Workout
            {
                UserId = user.Id,
                Date = new DateOnly(2026, 3, 1),
                StartedAt = new DateTimeOffset(2026, 3, 1, 8, i, 0, TimeSpan.Zero),
                Title = withDetails ? OptionalDetailsTestData.Title : null,
                Location = withDetails ? OptionalDetailsTestData.Location : null,
                Notes = withDetails ? OptionalDetailsTestData.Notes : null,
                BodyweightKg = withDetails ? OptionalDetailsTestData.BodyweightKg : null,
            });
        }
        await context.SaveChangesAsync();
        return user.Id;
    }

    // The clearing block holds BEGIN; SET LOCAL; the counting statement; COMMIT — sent as one
    // multi-statement command, as the Neon SQL editor would. Only the SELECT returns rows.
    private static async Task<(long Accounts, long Workouts)> RunClearingAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 2 && reader.GetName(0) == "accounts_cleared" && await reader.ReadAsync())
            {
                return (reader.GetInt64(0), reader.GetInt64(1));
            }
        }
        while (await reader.NextResultAsync());
        throw new InvalidOperationException("The clearing SQL returned no accounts_cleared/workouts_cleared row.");
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    // The ```sql fence that follows each marker comment in retention.md.
    private static (string Clear, string Verify) ReadDocumentedSql()
    {
        var text = File.ReadAllText(FindRetentionDocument());
        return (FencedSqlAfter(text, "<!-- transition-clearing-sql -->"), FencedSqlAfter(text, "<!-- transition-verify-sql -->"));
    }

    private static string FencedSqlAfter(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"retention.md has no {marker} marker.");
        var start = text.IndexOf("```sql\n", at, StringComparison.Ordinal) + "```sql\n".Length;
        var end = text.IndexOf("```", start, StringComparison.Ordinal);
        return text[start..end];
    }

    // The tests run from GymNotebook.Tests/bin/<config>/<tfm>/; walk up to the repository.
    private static string FindRetentionDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "privacy", "retention.md")))
        {
            directory = directory.Parent;
        }
        return directory is null
            ? throw new FileNotFoundException("docs/privacy/retention.md not found above the test output.")
            : Path.Combine(directory.FullName, "docs", "privacy", "retention.md");
    }
}
