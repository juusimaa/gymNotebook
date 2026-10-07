using GymNotebook.Api;

namespace GymNotebook.Tests;

// specs/002 tasks.md T014–T015: every email links to APP_URL, and the tokens travel in the
// URL fragment (spec FR-010), in both the text and the HTML part.
public class EmailTemplatesTests
{
    private const string AppUrl = "https://gymnotebook.fit";
    private const string Token = "header.payload.signature";
    private const string To = "ann@example.com";

    private static readonly EmailTemplates _templates = new(new EmailOptions
    {
        Backend = EmailOptions.MemoryBackend,
        From = "Gym Notebook <no-reply@example.com>",
        AppUrl = AppUrl,
        DailyCap = EmailOptions.DefaultDailyCap,
    });

    public static TheoryData<string, string> LinkedTemplates() => new()
    {
        { "confirmation", $"{AppUrl}/verify-email#token={Token}" },
        { "finish_signup", $"{AppUrl}/reset-password#token={Token}" },
        { "password_reset", $"{AppUrl}/reset-password#token={Token}" },
    };

    [Theory]
    [MemberData(nameof(LinkedTemplates))]
    public void Template_WithToken_LinksToFrontendRouteWithTokenInFragment(string kind, string expectedLink)
    {
        // Act
        var message = Render(kind);

        // Assert
        Assert.Equal(kind, message.Kind);
        Assert.Equal(To, message.To);
        Assert.Contains(expectedLink, message.Text);
        Assert.Contains($"href=\"{expectedLink}\"", message.Html);

        // Never as a query string, which browsers send to the server and into Referer.
        Assert.DoesNotContain("?token=", message.Text);
        Assert.DoesNotContain("?token=", message.Html);
    }

    [Fact]
    public void AlreadyRegistered_NoToken_LinksToSignInWithoutToken()
    {
        // Act
        var message = _templates.AlreadyRegistered(To);

        // Assert: it only points at the app; it must not sign anyone in.
        Assert.Contains($"{AppUrl}/", message.Text);
        Assert.Contains($"href=\"{AppUrl}/\"", message.Html);
        Assert.DoesNotContain("token", message.Text);
        Assert.DoesNotContain("token", message.Html);
    }

    [Fact]
    public void Confirmation_AddressWithMarkup_IsHtmlEncoded()
    {
        // Arrange: an address is user input, and the HTML goes out in the app's name.
        const string hostile = "<script>alert(1)</script>@example.com";

        // Act
        var message = _templates.Confirmation(hostile, Token);

        // Assert
        Assert.DoesNotContain("<script>", message.Html);
        Assert.Contains("&lt;script&gt;", message.Html);
    }

    [Fact]
    public void ToString_Message_ShowsKindOnly()
    {
        // Arrange
        var message = _templates.PasswordReset(To, Token);

        // Act
        var text = message.ToString();

        // Assert: a message logged by accident leaks neither the address nor the link.
        Assert.Contains("password_reset", text);
        Assert.DoesNotContain(To, text);
        Assert.DoesNotContain(Token, text);
    }

    private static EmailMessage Render(string kind) => kind switch
    {
        "confirmation" => _templates.Confirmation(To, Token),
        "finish_signup" => _templates.FinishSignup(To, Token),
        "password_reset" => _templates.PasswordReset(To, Token),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
