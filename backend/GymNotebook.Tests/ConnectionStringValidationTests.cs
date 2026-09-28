using GymNotebook.Api;

namespace GymNotebook.Tests;

// The startup check on ConnectionStrings:Default, without a host. The point of the check
// is what the failure does NOT say: no part of the configured value may appear in the
// exception, because an unhandled startup exception is written to the production logs.
public class ConnectionStringValidationTests
{
    private const string FakePassword = "not-a-real-password-123";

    [Fact]
    public void EnsureValid_KeyValueConnectionString_DoesNotThrow()
    {
        // Arrange
        var connectionString = $"Host=localhost;Database=gym;Username=gym;Password={FakePassword}";

        // Act
        var exception = Record.Exception(() => ConnectionStringValidation.EnsureValid(connectionString));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void EnsureValid_PostgresUrl_ThrowsWithoutRevealingValue()
    {
        // Arrange: the URL form that crashed production on 2026-09-23.
        var connectionString = $"postgresql://owner:{FakePassword}@db.example.test/gym?sslmode=require";

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => ConnectionStringValidation.EnsureValid(connectionString));

        // Assert: the setting is named, the value (and the inner exception quoting it) is not.
        Assert.Contains("ConnectionStrings:Default", exception.Message);
        Assert.DoesNotContain(FakePassword, exception.ToString());
        Assert.DoesNotContain("db.example.test", exception.ToString());
        Assert.Null(exception.InnerException);
    }
}
