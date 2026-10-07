using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// A suspended account (SignInSuspendedAt set by the operator during a restore fallback,
// research R6 Q2c / P3) cannot sign in or use a token. Login checks the password first,
// so only someone who already knows it learns that the account is suspended (Q5).
public class SuspensionTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private const string Password = "correct-horse-battery-staple";

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Login_SuspendedAccountWrongPassword_Returns401()
    {
        // Arrange
        var (email, _) = await SeedSuspendedUserAsync();

        // Act
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(email, "not-the-password"));

        // Assert: the same bare 401 as any wrong password — nothing about suspension.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_SuspendedAccountCorrectPassword_Returns403AccountSuspended()
    {
        // Arrange
        var (email, _) = await SeedSuspendedUserAsync();

        // Act
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(email, Password));

        // Assert: a code the UI maps to the privacy contact path, and no token.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("account_suspended", body?.Code);
    }

    [Fact]
    public async Task OnTokenValidated_SuspendedAccountToken_Returns401()
    {
        // Arrange: a token that would be valid in every other respect.
        var (_, userId) = await SeedSuspendedUserAsync();
        var token = TwoHostGymNotebookFixture.Token(userId);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_SuspendedUnconfirmedAccountCorrectPassword_Returns403AccountSuspended()
    {
        // Arrange: suspended *and* never confirmed.
        var (email, _) = await SeedSuspendedUserAsync(confirmed: false);

        // Act
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(email, Password));

        // Assert: suspension wins over email_not_verified (specs/002 Story 2, scenario 3),
        // so the user is pointed at the privacy contact, not at an inbox.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("account_suspended", body?.Code);
    }

    // Seeded directly (the class makes several logins; registration would add to the same
    // per-host rate-limit bucket for no reason) and suspended the way the operator would.
    private async Task<(string Email, int UserId)> SeedSuspendedUserAsync(bool confirmed = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            DisplayName = $"user-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            EmailVerifiedAt = confirmed ? DateTimeOffset.UtcNow : null,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SignInSuspendedAt, DateTimeOffset.UtcNow));
        return (user.Email, user.Id);
    }
}
