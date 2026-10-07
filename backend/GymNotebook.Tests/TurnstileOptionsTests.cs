using GymNotebook.Api;
using Microsoft.Extensions.Configuration;

namespace GymNotebook.Tests;

// specs/002 plan D9 (tasks.md T050–T051): the Turnstile settings are checked at boot. Pure
// configuration in, options or an exception out, like EmailOptionsTests.
public class TurnstileOptionsTests
{
    private const string Secret = "turnstile-secret-that-must-never-be-echoed";

    [Theory]
    [InlineData(null)]
    [InlineData("")]   // what Compose passes for a variable left blank in .env
    [InlineData("  ")]
    public void Load_NoSecret_IsOff(string? secret)
    {
        // Arrange
        var configuration = Config(("TURNSTILE_SECRET_KEY", secret), ("TURNSTILE_HOSTNAMES", null));

        // Act
        var options = TurnstileOptions.Load(configuration);

        // Assert
        Assert.False(options.Enabled);
        Assert.Null(options.SecretKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")] // commas but no names
    public void Load_SecretWithoutHostnames_ThrowsWithoutTheSecret(string? hostnames)
    {
        // Arrange
        var configuration = Config(("TURNSTILE_SECRET_KEY", Secret), ("TURNSTILE_HOSTNAMES", hostnames));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => TurnstileOptions.Load(configuration));

        // Assert
        Assert.Contains("TURNSTILE_HOSTNAMES", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message);
    }

    [Fact]
    public void Load_SecretAndHostnames_IsOnWithTrimmedCaseInsensitiveHostnames()
    {
        // Arrange
        var configuration = Config(("TURNSTILE_SECRET_KEY", Secret), ("TURNSTILE_HOSTNAMES", "gymnotebook.fit, www.gymnotebook.fit"));

        // Act
        var options = TurnstileOptions.Load(configuration);

        // Assert
        Assert.True(options.Enabled);
        Assert.Equal(2, options.Hostnames.Count);
        Assert.Contains("GymNotebook.fit", options.Hostnames);
        Assert.Contains("www.gymnotebook.fit", options.Hostnames);
    }

    // A record would print SecretKey in its generated ToString; the options are a class.
    [Fact]
    public void ToString_Enabled_DoesNotContainTheSecret()
    {
        // Arrange
        var options = TurnstileOptions.Load(Config(("TURNSTILE_SECRET_KEY", Secret), ("TURNSTILE_HOSTNAMES", "gymnotebook.fit")));

        // Act
        var text = options.ToString();

        // Assert
        Assert.DoesNotContain(Secret, text);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
