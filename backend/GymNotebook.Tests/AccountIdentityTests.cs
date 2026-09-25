using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GymNotebook.Tests;

// The account's privacy identity (specs/001 data-model.md → User additions, research R5):
// the migration backfills a genuine UUID per existing account and never seeds an
// acknowledgement, registration generates one server-side, and a username registered
// again after a deletion is a different account. Also the R6 Q2d invariant that account
// deletion is the only code that removes User rows.
[Collection("Lifecycle")]
public class AccountIdentityTests(TwoHostGymNotebookFixture db)
{
    // The migration just before AddPrivacyAccountIdentity: the schema existing
    // production rows were created under.
    private const string MigrationBeforePrivacy = "20260914091451_AddWorkoutDomain";

    // The ways C# in this codebase could delete a user row, for the Q2d scan below.
    private static readonly Regex[] _userRemovalPatterns =
    [
        new(@"\bUsers\s*\.\s*Remove(Range)?\s*\(", RegexOptions.IgnoreCase),                   // db.Users.Remove(user)
        new(@"\bRemove(Range)?\s*\(\s*(user|users|caller|account)\b", RegexOptions.IgnoreCase),  // db.Remove(user)
        new(@"\bDELETE\s+FROM\s+""?users\b", RegexOptions.IgnoreCase),                          // raw SQL
        new(@"\bUsers\b[^;]*\bExecuteDelete(Async)?\b", RegexOptions.Singleline),                // bulk delete, one statement
    ];

    [Fact]
    public async Task Migrate_ExistingUsers_BackfillsDistinctUuidsAndNullAcknowledgement()
    {
        // Arrange: a fresh database at the pre-feature schema, with existing accounts.
        var connectionString = await db.CreateEmptyDatabaseAsync();
        await using (var context = db.NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(MigrationBeforePrivacy);
        }
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO users (username, password_hash, token_version) SELECT 'existing-' || n, 'hash', 0 FROM generate_series(1, 3) AS n",
                connection);
            await insert.ExecuteNonQueryAsync();
        }

        // Act: apply the rest, including this feature's migration.
        await using var migrated = db.NewContext(connectionString);
        await migrated.Database.MigrateAsync();

        // Assert
        var users = await migrated.Users.AsNoTracking().ToListAsync();
        Assert.Equal(3, users.Count);
        Assert.All(users, u => Assert.NotEqual(Guid.Empty, u.PrivacyAccountId));
        Assert.Equal(3, users.Select(u => u.PrivacyAccountId).Distinct().Count());
        Assert.All(users, u =>
        {
            Assert.Null(u.AcknowledgedPrivacyNoticeVersion);
            Assert.Null(u.PrivacyNoticeAcknowledgedAt);
            Assert.Null(u.SignInSuspendedAt);
        });
    }

    [Fact]
    public async Task Register_NewAccount_GeneratesPrivacyAccountIdServerSide()
    {
        // Arrange
        await using var host = db.CreateHost();
        using var client = host.CreateClient();
        var username = $"user-{Guid.NewGuid():N}";

        // Act
        var response = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(username, "correct-horse-battery-staple", null));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await FindUserAsync(username);
        Assert.NotEqual(Guid.Empty, user.PrivacyAccountId);
        Assert.Null(user.AcknowledgedPrivacyNoticeVersion);
        Assert.Null(user.PrivacyNoticeAcknowledgedAt);
    }

    [Fact]
    public async Task Register_UsernameReusedAfterDeletion_GetsDifferentPrivacyAccountId()
    {
        // Arrange: an account registered, then deleted.
        await using var host = db.CreateHost();
        using var client = host.CreateClient();
        var username = $"user-{Guid.NewGuid():N}";
        var request = new RegisterRequest(username, "correct-horse-battery-staple", null);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/register", request)).StatusCode);
        var first = await FindUserAsync(username);
        await db.ExecuteAsync("DELETE FROM users WHERE id = @id", first.Id);

        // Act: someone registers the same username.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/register", request)).StatusCode);
        var second = await FindUserAsync(username);

        // Assert: distinguishable from the deleted account in log lines and restore diffs.
        Assert.NotEqual(first.PrivacyAccountId, second.PrivacyAccountId);
    }

    // R6 Q2d: account deletion is the only operation that removes a User row, because the
    // restore reconciliation treats a missing account as a deletion to re-apply. This scans
    // the API's source for anything that deletes users and fails unless it's in an allowed
    // file. Only the account deletion itself (US4) is allowed; any other path that removes
    // users must revisit research R6 first.
    [Fact]
    public void ApiSource_RemovesUserRows_OnlyInAccountDeletionCode()
    {
        // Arrange
        string[] allowedFiles = ["AccountDeletion.cs"];
        var apiDirectory = FindApiSourceDirectory();

        // Act: every C# file except generated migrations (which only drop/create schema).
        var offenders = Directory.EnumerateFiles(apiDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnder(path, Path.Combine(apiDirectory, "Migrations"))
                           && !IsUnder(path, Path.Combine(apiDirectory, "obj"))
                           && !IsUnder(path, Path.Combine(apiDirectory, "bin")))
            .Where(path => !allowedFiles.Contains(Path.GetRelativePath(apiDirectory, path)))
            .Where(path => _userRemovalPatterns.Any(p => p.IsMatch(File.ReadAllText(path))))
            .Select(path => Path.GetRelativePath(apiDirectory, path))
            .ToList();

        // Assert
        Assert.Empty(offenders);
    }

    [Fact]
    public void ApiSource_ScanPatterns_DetectUserRemoval()
    {
        // Arrange: guards the scan above against passing because its patterns match
        // nothing — each known way of deleting a user must be caught.
        string[] samples =
        [
            "db.Users.Remove(user);",
            "db.Remove(user);",
            "await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);",
            "\"DELETE FROM users WHERE id = @id\"",
        ];

        // Act + Assert
        Assert.All(samples, sample => Assert.Contains(_userRemovalPatterns, p => p.IsMatch(sample)));
    }

    private async Task<User> FindUserAsync(string username)
    {
        await using var context = db.NewContext();
        return await context.Users.AsNoTracking().SingleAsync(u => u.Username == username);
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // The tests run from GymNotebook.Tests/bin/<config>/<tfm>/; walk up to the backend
    // folder that holds both projects.
    private static string FindApiSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "GymNotebook.Api")))
        {
            directory = directory.Parent;
        }
        return directory is null
            ? throw new DirectoryNotFoundException("GymNotebook.Api source folder not found above the test output.")
            : Path.Combine(directory.FullName, "GymNotebook.Api");
    }
}
