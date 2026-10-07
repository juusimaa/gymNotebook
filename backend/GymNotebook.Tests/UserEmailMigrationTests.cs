using GymNotebook.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GymNotebook.Tests;

// The AddUserEmail migration (specs/002 plan D11, FR-022). Existing accounts are wiped by
// the operator before it deploys, not migrated; if the wipe is forgotten, the migration
// must stop the deploy rather than invent addresses. It does that by adding email NOT NULL
// with no default, which PostgreSQL refuses on a table with rows.
[Collection("Lifecycle")]
public class UserEmailMigrationTests(TwoHostGymNotebookFixture db)
{
    private const string MigrationBeforeEmail = "20261006133659_AddEmailSends";

    [Fact]
    public async Task Migrate_UsersTableNotEmpty_FailsWithoutChangingSchema()
    {
        // Arrange: a database one step before the email schema, with one existing account
        // — the state production would be in if the wipe were skipped.
        var connectionString = await db.CreateEmptyDatabaseAsync();
        await using (var context = db.NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeEmail);
        }
        await ExecuteAsync(connectionString,
            "INSERT INTO users (username, password_hash, token_version, privacy_account_id) VALUES ('existing', 'hash', 0, gen_random_uuid())");

        // Act
        await using var migrating = db.NewContext(connectionString);
        var exception = await Record.ExceptionAsync(() => migrating.Database.MigrateAsync());

        // Assert: refused for the NOT NULL column (23502 not_null_violation), and — since
        // the migration runs in one transaction — nothing of it was applied: the column is
        // still called username and the account is untouched.
        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.Equal(PostgresErrorCodes.NotNullViolation, postgres.SqlState);
        Assert.Equal("email", postgres.ColumnName);
        Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM users WHERE username = 'existing'"));
        Assert.Equal(0L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '%_AddUserEmail'"));
    }

    [Fact]
    public async Task Migrate_UsersTableEmpty_AddsEmailSchema()
    {
        // Arrange: the same starting point, wiped.
        var connectionString = await db.CreateEmptyDatabaseAsync();
        await using (var context = db.NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeEmail);
        }

        // Act
        await using var migrated = db.NewContext(connectionString);
        await migrated.Database.MigrateAsync();

        // Assert: display names may repeat now; addresses may not.
        migrated.Users.AddRange(NewUser("ann@example.test", "Ann"), NewUser("bob@example.test", "Ann"));
        await migrated.SaveChangesAsync();

        migrated.Users.Add(NewUser("ann@example.test", "Someone else"));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => migrated.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal("ix_users_email", postgres.ConstraintName);
    }

    private static User NewUser(string email, string displayName) => new()
    {
        Email = email,
        DisplayName = displayName,
        PasswordHash = "hash",
        PrivacyAccountId = Guid.NewGuid(),
    };

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
