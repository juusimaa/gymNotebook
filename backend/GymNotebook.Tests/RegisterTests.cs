using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// POST /auth/register (specs/002 Story 1, contracts/api.md → Register): one answer for
// every valid request, and the difference only in the inbox. Every test in the class
// shares one host and so one "auth" rate-limit bucket (10 per minute); the existing
// accounts are seeded directly so the class stays inside it.
public class RegisterTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_NewAddress_Returns202AndSendsConfirmationLink()
    {
        // Arrange
        var email = EmailTestSupport.UniqueEmail();

        // Act
        var response = await EmailTestSupport.RegisterAsync(_client, email);

        // Assert: no body, no token — the account can't sign in yet.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var message = await EmailTestSupport.WaitForMessageAsync(factory.Services, email, "confirmation");
        Assert.Contains($"{GymNotebookFactory.AppUrl}/verify-email#token=", message.Text);
        var link = JwtTokenFactory.ReadLinkToken(EmailTestSupport.LinkToken(message), LinkPurpose.Verify, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);
        Assert.Equal(LinkTokenStatus.Valid, link.Status);
        Assert.Equal(email, link.Email);
    }

    [Fact]
    public async Task Register_MixedCaseAddressWithSpaces_StoresUnconfirmedAccountWithNormalizedFields()
    {
        // Arrange
        var email = EmailTestSupport.UniqueEmail();

        // Act
        var response = await _client.PostAsJsonAsync("/auth/register",
            new RegisterRequest($"  {email.ToUpperInvariant()} ", EmailTestSupport.Password, "  Ann  ", null));

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var user = await FindUserAsync(email);
        Assert.Equal("Ann", user.DisplayName);
        Assert.Null(user.EmailVerifiedAt);
        Assert.True(BCrypt.Net.BCrypt.Verify(EmailTestSupport.Password, user.PasswordHash));
    }

    [Fact]
    public async Task Register_ConfirmedAddress_LeavesAccountUnchangedAndSendsAlreadyRegistered()
    {
        // Arrange
        var existing = await EmailTestSupport.SeedUserAsync(factory.Services);

        // Act: someone tries to take the address with their own password.
        var response = await EmailTestSupport.RegisterAsync(_client, existing.Email, "an-attackers-password", "Mallory");

        // Assert: Story 1, scenario 4.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = await EmailTestSupport.WaitForMessageAsync(factory.Services, existing.Email, "already_registered");
        Assert.DoesNotContain("#token=", message.Text);

        var after = await FindUserAsync(existing.Email);
        Assert.Equal(existing.PasswordHash, after.PasswordHash);
        Assert.Equal(existing.DisplayName, after.DisplayName);
        Assert.Equal(existing.TokenVersion, after.TokenVersion);
    }

    [Fact]
    public async Task Register_UnconfirmedAddress_DiscardsPasswordAndSendsFinishSignupResetLink()
    {
        // Arrange: someone already signed up with this address and never confirmed.
        var existing = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act
        var response = await EmailTestSupport.RegisterAsync(_client, existing.Email, "a-second-password", "Someone");

        // Assert: Story 1, scenario 5 — same answer, nothing changed, and the inbox gets a
        // *reset* link so whoever reads it chooses the password.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = await EmailTestSupport.WaitForMessageAsync(factory.Services, existing.Email, "finish_signup");
        Assert.Contains($"{GymNotebookFactory.AppUrl}/reset-password#token=", message.Text);
        var link = JwtTokenFactory.ReadLinkToken(EmailTestSupport.LinkToken(message), LinkPurpose.Reset, GymNotebookFactory.JwtSecret, DateTimeOffset.UtcNow);
        Assert.Equal(LinkTokenStatus.Valid, link.Status);
        Assert.Equal(existing.Id, link.UserId);
        Assert.Equal(existing.TokenVersion, link.TokenVersion);

        var after = await FindUserAsync(existing.Email);
        Assert.Equal(existing.PasswordHash, after.PasswordHash);
        Assert.Null(after.EmailVerifiedAt);
    }

    [Fact]
    public async Task Register_NewConfirmedAndUnconfirmedAddresses_AnswersAreIdentical()
    {
        // Arrange: one address of each kind.
        var confirmed = await EmailTestSupport.SeedUserAsync(factory.Services);
        var unconfirmed = await EmailTestSupport.SeedUserAsync(factory.Services, confirmed: false);

        // Act
        var answers = new List<string>();
        foreach (var email in new[] { EmailTestSupport.UniqueEmail(), confirmed.Email, unconfirmed.Email })
        {
            answers.Add(await DescribeAsync(await EmailTestSupport.RegisterAsync(_client, email)));
        }

        // Assert: SC-002 — status, every header and the body, byte for byte.
        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task Register_SameNewAddressTwiceAtOnce_CreatesOneAccountAndAcceptsBoth()
    {
        // Arrange
        var email = EmailTestSupport.UniqueEmail();

        // Act: whichever INSERT loses the unique index is handled as "account exists".
        var responses = await Task.WhenAll(
            EmailTestSupport.RegisterAsync(_client, email),
            EmailTestSupport.RegisterAsync(_client, email));

        // Assert: one account, and the inbox got one confirmation plus one finish-signup —
        // the same outcome whether the two really overlapped or ran one after the other.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));

        var messages = await EmailTestSupport.WaitForMessagesAsync(factory.Services, email, count: 2);
        var kinds = messages.Select(m => m.Kind).Order().ToList();
        Assert.Equal(["confirmation", "finish_signup"], kinds);
    }

    // Everything a caller can observe about a response, as one comparable string.
    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
        var body = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync());
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n{body}";
    }

    private async Task<User> FindUserAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
    }
}

// Malformed input, which is safe to answer differently because it says nothing about
// accounts. Its own class (own host, own rate-limit bucket): six more registrations
// would push RegisterTests past the "auth" limit.
public class RegisterValidationTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    public static TheoryData<string?, string?, string?> InvalidRequests() => new()
    {
        { "not-an-address", EmailTestSupport.Password, "Ann" },
        { "  ", EmailTestSupport.Password, "Ann" },
        { "ann@example.test", "   ", "Ann" },
        // 73 bytes: one over what BCrypt reads (specs/002 FR-005).
        { "ann@example.test", new string('a', 73), "Ann" },
        { "ann@example.test", EmailTestSupport.Password, "   " },
        { "ann@example.test", EmailTestSupport.Password, new string('n', 51) },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Register_InvalidField_Returns400InvalidRequestAndCreatesNothing(string? email, string? password, string? displayName)
    {
        // Act
        var response = await _client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password, displayName, null));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == "ann@example.test"));
    }
}

// The gated-registration cases, while the invite code still exists (it goes in specs/002
// PR 5). A separate class because they need a different fixture (INVITE_CODE set) — see
// InviteCodeGymNotebookFactory for why that can't be a runtime toggle on the shared one.
public class InviteCodeGatedRegisterTests(InviteCodeGymNotebookFactory factory) : IClassFixture<InviteCodeGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static RegisterRequest Request(string? inviteCode) =>
        new(EmailTestSupport.UniqueEmail(), EmailTestSupport.Password, "Test user", inviteCode);

    [Fact]
    public async Task Register_WithoutInviteCode_Returns403()
    {
        var response = await _client.PostAsJsonAsync("/auth/register", Request(null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_WrongInviteCode_Returns403()
    {
        var response = await _client.PostAsJsonAsync("/auth/register", Request("not-the-code"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_CorrectInviteCode_Returns202()
    {
        var response = await _client.PostAsJsonAsync("/auth/register", Request(InviteCodeGymNotebookFactory.RequiredInviteCode));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
