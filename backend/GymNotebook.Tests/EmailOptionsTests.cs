using GymNotebook.Api;
using Microsoft.Extensions.Configuration;

namespace GymNotebook.Tests;

// specs/002 plan D7 (tasks.md T011, T015): the email settings are checked at boot. Pure
// configuration in, options or an exception out — no host and no database needed.
public class EmailOptionsTests
{
    private const string ApiKey = "re_test_key_that_must_never_be_echoed";

    [Theory]
    [InlineData("console")]
    [InlineData("memory")]
    [InlineData(null)] // unset falls back to console, so a forgotten setting fails too
    [InlineData("")]   // what Compose passes for a variable left blank in .env
    public void Load_LocalOnlyBackendInProduction_Throws(string? backend)
    {
        // Arrange
        var configuration = Config(("Email:Backend", backend));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => EmailOptions.Load(configuration, isProduction: true));

        // Assert
        Assert.Contains("not allowed in Production", exception.Message);
    }

    [Theory]
    [InlineData("console")]
    [InlineData("memory")]
    [InlineData("MEMORY")] // case-insensitive, like the reference's EMAIL_BACKEND
    public void Load_LocalOnlyBackendOutsideProduction_ReturnsDefaults(string backend)
    {
        // Arrange
        var configuration = Config(("Email:Backend", backend));

        // Act
        var options = EmailOptions.Load(configuration, isProduction: false);

        // Assert
        Assert.Equal(backend.ToLowerInvariant(), options.Backend);
        Assert.Equal("http://localhost:5173", options.AppUrl);
        Assert.Equal(EmailOptions.DefaultDailyCap, options.DailyCap);
        Assert.Null(options.ResendApiKey);
    }

    [Fact]
    public void Load_UnknownBackend_Throws()
    {
        // Arrange
        var configuration = Config(("Email:Backend", "smtp"));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => EmailOptions.Load(configuration, isProduction: false));
    }

    [Theory]
    [InlineData(null, "Gym Notebook <no-reply@mail.gymnotebook.fit>", "https://gymnotebook.fit")]
    [InlineData(ApiKey, null, "https://gymnotebook.fit")]
    [InlineData(ApiKey, "Gym Notebook <no-reply@mail.gymnotebook.fit>", null)]
    [InlineData("  ", "Gym Notebook <no-reply@mail.gymnotebook.fit>", "https://gymnotebook.fit")]
    public void Load_ResendMissingSetting_ThrowsNamingSettingsNotValues(string? apiKey, string? from, string? appUrl)
    {
        // Arrange
        var configuration = Config(
            ("Email:Backend", "resend"), ("RESEND_API_KEY", apiKey), ("EMAIL_FROM", from), ("APP_URL", appUrl));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => EmailOptions.Load(configuration, isProduction: true));

        // Assert: the message tells the operator what to set, and never quotes the key.
        Assert.Contains("RESEND_API_KEY", exception.Message);
        Assert.DoesNotContain(ApiKey, exception.Message);
    }

    [Fact]
    public void Load_ResendComplete_ReturnsOptionsWithTrimmedAppUrl()
    {
        // Arrange
        var configuration = Config(
            ("Email:Backend", "resend"),
            ("RESEND_API_KEY", ApiKey),
            ("EMAIL_FROM", "Gym Notebook <no-reply@mail.gymnotebook.fit>"),
            ("APP_URL", "https://gymnotebook.fit/"),
            ("EMAIL_DAILY_CAP", "50"));

        // Act
        var options = EmailOptions.Load(configuration, isProduction: true);

        // Assert
        Assert.Equal(EmailOptions.ResendBackend, options.Backend);
        Assert.Equal(ApiKey, options.ResendApiKey);
        Assert.Equal("https://gymnotebook.fit", options.AppUrl);
        Assert.Equal(50, options.DailyCap);
    }

    [Theory]
    [InlineData("gymnotebook.fit")]          // no scheme: links would be relative
    [InlineData("ftp://gymnotebook.fit")]
    [InlineData("javascript:alert(1)")]
    public void Load_AppUrlNotAbsoluteHttp_Throws(string appUrl)
    {
        // Arrange
        var configuration = Config(("Email:Backend", "memory"), ("APP_URL", appUrl));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => EmailOptions.Load(configuration, isProduction: false));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Load_DailyCapBelowOne_Throws(string cap)
    {
        // Arrange
        var configuration = Config(("Email:Backend", "memory"), ("EMAIL_DAILY_CAP", cap));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => EmailOptions.Load(configuration, isProduction: false));
    }

    [Fact]
    public void ToString_ResendOptions_DoesNotRevealApiKey()
    {
        // Arrange
        var configuration = Config(
            ("Email:Backend", "resend"), ("RESEND_API_KEY", ApiKey), ("EMAIL_FROM", "x <x@example.com>"), ("APP_URL", "https://example.com"));
        var options = EmailOptions.Load(configuration, isProduction: true);

        // Act
        var text = options.ToString();

        // Assert: EmailOptions is a class, not a record, so nothing prints the key.
        Assert.DoesNotContain(ApiKey, text);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
