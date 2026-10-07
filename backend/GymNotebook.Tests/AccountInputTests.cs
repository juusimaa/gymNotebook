using GymNotebook.Api;

namespace GymNotebook.Tests;

// The account input rules (specs/002 data-model.md, FR-001, FR-002, FR-005), no host.
public class AccountInputTests
{
    [Fact]
    public void NormalizeEmail_CapitalsAndSpaces_TrimmedAndLowercased()
    {
        Assert.Equal("ann@example.com", AccountInput.NormalizeEmail("  Ann@Example.COM "));
        Assert.Equal("", AccountInput.NormalizeEmail(null));
    }

    [Theory]
    [InlineData("ann@example.com")]
    [InlineData("a@b")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    public void IsValidEmail_OneAtWithSomethingEitherSide_True(string email)
    {
        Assert.True(AccountInput.IsValidEmail(email));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ann")]
    [InlineData("@example.com")]
    [InlineData("ann@")]
    [InlineData("ann@@example.com")]
    [InlineData("a@b@c")]
    [InlineData("ann smith@example.com")]
    public void IsValidEmail_Malformed_False(string email)
    {
        Assert.False(AccountInput.IsValidEmail(email));
    }

    [Fact]
    public void IsValidEmail_LongerThan254_False()
    {
        var local = new string('a', 64);
        var tooLong = $"{local}@{new string('b', 254 - local.Length)}";

        Assert.True(AccountInput.IsValidEmail(tooLong[..254]));
        Assert.False(AccountInput.IsValidEmail(tooLong));
    }

    [Fact]
    public void IsValidPassword_CountsUtf8BytesNotCharacters()
    {
        // "ä" is two bytes in UTF-8: 36 of them are 72 bytes, 37 are 74.
        Assert.True(AccountInput.IsValidPassword(new string('ä', 36)));
        Assert.False(AccountInput.IsValidPassword(new string('ä', 37)));
        Assert.True(AccountInput.IsValidPassword(new string('a', 72)));
        Assert.False(AccountInput.IsValidPassword(new string('a', 73)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidPassword_Blank_False(string? password)
    {
        Assert.False(AccountInput.IsValidPassword(password));
    }

    [Fact]
    public void NormalizeDisplayName_TrimsAndBoundsTo50()
    {
        Assert.Equal("Ann", AccountInput.NormalizeDisplayName("  Ann "));
        Assert.Equal(new string('n', 50), AccountInput.NormalizeDisplayName(new string('n', 50)));
        Assert.Null(AccountInput.NormalizeDisplayName(new string('n', 51)));
        Assert.Null(AccountInput.NormalizeDisplayName("   "));
        Assert.Null(AccountInput.NormalizeDisplayName(null));
    }
}
