using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace GymNotebook.Tests;

public sealed class RenewTokenGymNotebookFactory : GymNotebookFactory
{
    public OffsetTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}

public class RenewTokenTests(RenewTokenGymNotebookFactory factory) : IClassFixture<RenewTokenGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<HttpResponseMessage> RenewAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static JwtSecurityToken Read(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    private void AssertNewAuthTime(string token)
    {
        var seconds = long.Parse(Read(token).Claims.Single(c => c.Type == JwtTokenFactory.AuthTimeClaim).Value);
        Assert.InRange(seconds, factory.Clock.GetUtcNow().ToUnixTimeSeconds() - 5,
            factory.Clock.GetUtcNow().ToUnixTimeSeconds());
    }

    private static string SignedToken(params Claim[] claims)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GymNotebookFactory.JwtSecret)),
            SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(claims: claims,
            expires: DateTime.UtcNow.AddMinutes(20), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    [Fact]
    public async Task RenewToken_ValidToken_ReturnsFreshTokenWithSameAuthTime()
    {
        // Arrange: an older token still has ten minutes left, but should gain 30.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var now = factory.Clock.GetUtcNow();
        var old = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret,
            GymNotebookFactory.JwtExpiryMinutes, now.AddMinutes(-20));

        // Act
        var response = await RenewAsync(old);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var fresh = (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
        Assert.Equal(Read(old).Claims.Single(c => c.Type == JwtTokenFactory.AuthTimeClaim).Value,
            Read(fresh).Claims.Single(c => c.Type == JwtTokenFactory.AuthTimeClaim).Value);
        Assert.Equal(Read(old).Subject, Read(fresh).Subject);
        Assert.Equal(Read(old).Claims.Single(c => c.Type == "tv").Value,
            Read(fresh).Claims.Single(c => c.Type == "tv").Value);
        Assert.True(Read(fresh).ValidTo > Read(old).ValidTo.AddMinutes(15));
    }

    [Fact]
    public async Task RenewToken_PastCap_Returns403RenewalRefused()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var now = factory.Clock.GetUtcNow();
        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret,
            GymNotebookFactory.JwtExpiryMinutes, now, now.AddHours(-13));

        var response = await RenewAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("renewal_refused", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task RenewToken_WithoutAuthTime_Returns403()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = SignedToken(new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("tv", user.TokenVersion.ToString()));

        var response = await RenewAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("renewal_refused", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task RenewToken_AfterPasswordChange_Returns401()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var old = EmailTestSupport.SessionToken(user);
        using var change = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(
                EmailTestSupport.Password, "a-new-correct-horse-battery-staple")),
        };
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", old);
        var changed = await _client.SendAsync(change);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var response = await RenewAsync(old);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RenewToken_LinkToken_Returns401()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset,
            GymNotebookFactory.JwtSecret, factory.Clock.GetUtcNow());

        var response = await RenewAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RenewToken_Expired_Returns401()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        // Ten minutes beyond expiry also exceeds the JWT handler's clock skew.
        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret,
            GymNotebookFactory.JwtExpiryMinutes, factory.Clock.GetUtcNow().AddMinutes(-40));

        var response = await RenewAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RenewToken_JustExpired_Returns401()
    {
        // JWT bearer validation tolerates five minutes of clock skew; renewal does not.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret,
            GymNotebookFactory.JwtExpiryMinutes, factory.Clock.GetUtcNow().AddMinutes(-31));

        var response = await RenewAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RenewToken_Suspended_Returns403()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var token = EmailTestSupport.SessionToken(user);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.SignInSuspendedAt, factory.Clock.GetUtcNow()));
        }

        var response = await RenewAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("account_suspended", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Login_ValidPassword_StartsAuthTime()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);

        var response = await _client.PostAsJsonAsync("/auth/login",
            new LoginRequest(user.Email, EmailTestSupport.Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNewAuthTime((await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token);
    }

    [Fact]
    public async Task ChangePassword_ValidPassword_StartsAuthTime()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var old = EmailTestSupport.SessionToken(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(
                EmailTestSupport.Password, "another-new-correct-horse-battery-staple")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", old);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNewAuthTime((await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token);
    }

    [Fact]
    public async Task ConfirmPasswordReset_ValidLink_StartsAuthTime()
    {
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var link = JwtTokenFactory.CreateLinkToken(user, LinkPurpose.Reset,
            GymNotebookFactory.JwtSecret, factory.Clock.GetUtcNow());

        var response = await _client.PostAsJsonAsync("/auth/password-reset/confirm",
            new ConfirmPasswordResetRequest(link, "reset-correct-horse-battery-staple"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNewAuthTime((await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token);
    }
}
