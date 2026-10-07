using GymNotebook.Api;

namespace GymNotebook.Tests;

// JwtTokenFactory's link tokens on their own, no host (specs/002 plan D2, data-model.md →
// Link tokens): what each purpose carries, how long it lasts, and the read order
// signature → purpose → expiry.
public class LinkTokenTests
{
    private const string Secret = GymNotebookFactory.JwtSecret;
    private static readonly DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly User _user = new()
    {
        Id = 42,
        Email = "ann@example.test",
        DisplayName = "Ann",
        PasswordHash = "",
        TokenVersion = 3,
        PrivacyAccountId = Guid.Empty,
    };

    [Fact]
    public void ReadLinkToken_VerifyTokenWithin48Hours_ValidWithUserAndEmail()
    {
        // Arrange
        var token = JwtTokenFactory.CreateLinkToken(_user, LinkPurpose.Verify, Secret, _now);

        // Act
        var result = JwtTokenFactory.ReadLinkToken(token, LinkPurpose.Verify, Secret, _now.AddHours(47));

        // Assert
        Assert.Equal(new LinkTokenResult(LinkTokenStatus.Valid, 42, "ann@example.test", 0), result);
    }

    [Fact]
    public void ReadLinkToken_ResetTokenWithinOneHour_ValidWithTokenVersion()
    {
        // Arrange
        var token = JwtTokenFactory.CreateLinkToken(_user, LinkPurpose.Reset, Secret, _now);

        // Act
        var result = JwtTokenFactory.ReadLinkToken(token, LinkPurpose.Reset, Secret, _now.AddMinutes(59));

        // Assert: the tv is what makes a reset link single-use (PR 4).
        Assert.Equal(new LinkTokenResult(LinkTokenStatus.Valid, 42, null, 3), result);
    }

    [Theory]
    [InlineData(LinkPurpose.Verify, 48)]
    [InlineData(LinkPurpose.Reset, 1)]
    public void ReadLinkToken_AtOrPastLifetime_Expired(LinkPurpose purpose, int lifetimeHours)
    {
        // Arrange
        var token = JwtTokenFactory.CreateLinkToken(_user, purpose, Secret, _now);

        // Act
        var result = JwtTokenFactory.ReadLinkToken(token, purpose, Secret, _now.AddHours(lifetimeHours));

        // Assert
        Assert.Equal(LinkTokenStatus.Expired, result.Status);
    }

    [Theory]
    [InlineData(LinkPurpose.Verify, LinkPurpose.Reset)]
    [InlineData(LinkPurpose.Reset, LinkPurpose.Verify)]
    public void ReadLinkToken_OtherPurposeEvenIfExpired_Invalid(LinkPurpose minted, LinkPurpose expected)
    {
        // Arrange: a week old, so expired whichever purpose — the wrong purpose must still
        // be what's reported.
        var token = JwtTokenFactory.CreateLinkToken(_user, minted, Secret, _now.AddDays(-7));

        // Act
        var result = JwtTokenFactory.ReadLinkToken(token, expected, Secret, _now);

        // Assert
        Assert.Equal(LinkTokenStatus.Invalid, result.Status);
    }

    [Fact]
    public void ReadLinkToken_SessionToken_Invalid()
    {
        // Arrange: a sign-in token has no purpose claim.
        var token = JwtTokenFactory.CreateToken(_user, Secret, 30);

        // Act
        var result = JwtTokenFactory.ReadLinkToken(token, LinkPurpose.Reset, Secret, DateTimeOffset.UtcNow);

        // Assert
        Assert.Equal(LinkTokenStatus.Invalid, result.Status);
    }

    [Fact]
    public void ReadLinkToken_SignedWithAnotherSecret_Invalid()
    {
        // Arrange
        var token = JwtTokenFactory.CreateLinkToken(_user, LinkPurpose.Verify, "another-secret-that-is-long-enough-for-hs256-0123456789", _now);

        // Act
        var result = JwtTokenFactory.ReadLinkToken(token, LinkPurpose.Verify, Secret, _now);

        // Assert
        Assert.Equal(LinkTokenStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ReadLinkToken_MissingOrMalformed_Invalid(string? token)
    {
        Assert.Equal(LinkTokenStatus.Invalid, JwtTokenFactory.ReadLinkToken(token, LinkPurpose.Verify, Secret, _now).Status);
    }
}
