using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/007 user story 1: POST /auth/demo creates, seeds and signs in a demo account.
public class DemoStartTests(DemoEnabledGymNotebookFactory factory) : IClassFixture<DemoEnabledGymNotebookFactory>
{
    [Fact]
    public async Task StartDemo_Valid_ReturnsTokenThatReadsSeededNotebook()
    {
        // Arrange
        var client = factory.CreateClient();

        // Act
        await DemoTestSupport.StartDemoAsync(client);

        // Assert: a demo for 24 hours, with pages, templates and a squat history. One
        // history point per session: squats are on the Lower days, about half of them.
        var me = await DemoTestSupport.MeAsync(client);
        Assert.Equal(DemoOptions.DisplayName, me.DisplayName);
        Assert.NotNull(me.DemoExpiresAt);
        Assert.InRange(me.DemoExpiresAt.Value, DateTimeOffset.UtcNow.AddHours(24).AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(24).AddMinutes(1));
        Assert.NotEmpty((await client.GetFromJsonAsync<List<WorkoutSummaryResponse>>("/workouts"))!);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<TemplateSummaryResponse>>("/templates"))!.Count);
        var squat = (await client.GetFromJsonAsync<List<ExerciseResponse>>("/exercises?search=Back%20Squat"))!.Single();
        var history = await client.GetFromJsonAsync<ExerciseHistoryResponse>($"/exercises/{squat.Id}/history");
        Assert.True(history!.Points.Count >= 15, $"{history.Points.Count} squat sessions");
    }

    [Fact]
    public async Task StartDemo_TwoStarts_GetSeparateAccounts()
    {
        // Arrange
        var first = factory.CreateClient();
        var second = factory.CreateClient();

        // Act
        await DemoTestSupport.StartDemoAsync(first);
        await DemoTestSupport.StartDemoAsync(second);

        // Assert: two accounts, and one's page is a 404 to the other (not a 403).
        Assert.NotEqual((await DemoTestSupport.MeAsync(first)).UserId, (await DemoTestSupport.MeAsync(second)).UserId);
        var firstPages = (await first.GetFromJsonAsync<List<WorkoutSummaryResponse>>("/workouts"))!.Select(w => w.Id).ToList();
        var secondPages = (await second.GetFromJsonAsync<List<WorkoutSummaryResponse>>("/workouts"))!.Select(w => w.Id).ToList();
        Assert.Empty(firstPages.Intersect(secondPages));
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync($"/workouts/{firstPages[0]}")).StatusCode);
    }

    [Theory]
    [InlineData(841)]
    [InlineData(-841)]
    [InlineData(null)]
    public async Task StartDemo_OffsetOutOfRange_Returns400(int? offset)
    {
        // Arrange
        var client = factory.CreateClient();
        var before = await DemoTestSupport.CountLiveDemosAsync(factory.Services);

        // Act
        var response = await client.PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, offset));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(before, await DemoTestSupport.CountLiveDemosAsync(factory.Services));
    }

    [Fact]
    public async Task StartDemo_Valid_AnswersNoStore()
    {
        // Arrange
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, 0));

        // Assert: a token must never be cached.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task GetMe_RegisteredAccount_HasNullDemoExpiresAt()
    {
        // Arrange
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", EmailTestSupport.SessionToken(user));

        // Act
        using var body = JsonDocument.Parse(await client.GetStringAsync("/auth/me"));

        // Assert: present and null, not missing.
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("demoExpiresAt").ValueKind);
    }
}

// The live-demo cap (research R3). Each test first expires every demo the earlier ones
// left, since all share this host's database and its cap of one.
public class DemoCapTests(DemoCapOneGymNotebookFactory factory) : IClassFixture<DemoCapOneGymNotebookFactory>
{
    [Fact]
    public async Task StartDemo_AtLiveCap_Returns503AndCreatesNothing()
    {
        // Arrange
        await DemoTestSupport.ExpireAsync(factory.Services);
        await DemoTestSupport.StartDemoAsync(factory.CreateClient());
        var users = await CountUsersAsync();

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, 180));

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("demo_unavailable", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(users, await CountUsersAsync());
    }

    [Fact]
    public async Task StartDemo_ExpiredDemosDoNotCountTowardCap()
    {
        // Arrange: the cap's one demo, expired.
        await DemoTestSupport.ExpireAsync(factory.Services);
        await DemoTestSupport.StartDemoAsync(factory.CreateClient());
        await DemoTestSupport.ExpireAsync(factory.Services);

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, 180));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<int> CountUsersAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync();
    }
}

// The flag (research R3): only the exact string "true" maps the routes.
public class DemoFlagOffTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    [Fact]
    public async Task StartDemo_FlagOff_Returns404()
    {
        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, 180));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResetDemo_FlagOff_Returns404()
    {
        // Arrange: a signed-in registered account, so only the missing route can explain it.
        var user = await EmailTestSupport.SeedUserAsync(factory.Services);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", EmailTestSupport.SessionToken(user));

        // Act
        var response = await DemoTestSupport.ResetAsync(client);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public class DemoFlagCapitalizedTests(DemoFlagCapitalizedGymNotebookFactory factory) : IClassFixture<DemoFlagCapitalizedGymNotebookFactory>
{
    [Fact]
    public async Task StartDemo_FlagCapitalized_Returns404()
    {
        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, 180));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

// The bot check fails closed, with the "demo" action.
public class DemoTurnstileTests(DemoTurnstileGymNotebookFactory factory) : IClassFixture<DemoTurnstileGymNotebookFactory>
{
    [Theory]
    [InlineData(StubSiteverifyHandler.Rejected)]
    [InlineData(StubSiteverifyHandler.PassSignup)]
    [InlineData(StubSiteverifyHandler.Unreachable)]
    [InlineData(null)]
    public async Task StartDemo_TurnstileRejects_Returns400CaptchaAndCreatesNothing(string? token)
    {
        // Arrange: refused, solved on another form, Cloudflare down, or no token at all.
        var before = await DemoTestSupport.CountLiveDemosAsync(factory.Services);

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/demo", new DemoStartRequest(token, 180));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("captcha", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(before, await DemoTestSupport.CountLiveDemosAsync(factory.Services));
    }

    [Fact]
    public async Task StartDemo_TurnstilePassesDemoAction_Returns200()
    {
        // Act + Assert
        await DemoTestSupport.StartDemoAsync(factory.CreateClient(), turnstileToken: StubSiteverifyHandler.PassDemo);
    }
}

public class DemoRateLimitTests(DemoRateLimitedGymNotebookFactory factory) : IClassFixture<DemoRateLimitedGymNotebookFactory>
{
    [Fact]
    public async Task StartDemo_OverPerIpLimit_Returns429()
    {
        // Arrange: the two starts the window allows.
        await DemoTestSupport.StartDemoAsync(factory.CreateClient());
        await DemoTestSupport.StartDemoAsync(factory.CreateClient());

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/demo", new DemoStartRequest(null, 180));

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}

// FR-016: with the privacy feature on and no consent yet, the seed's optional details are
// dropped exactly as a restored backup's would be, and the notice flow runs as usual.
public class DemoPrivacyStartTests(DemoPrivacyEnabledGymNotebookFactory factory) : IClassFixture<DemoPrivacyEnabledGymNotebookFactory>
{
    [Fact]
    public async Task StartDemo_PrivacyOnWithoutConsent_SeedsNoOptionalDetailsAndRequiresNotice()
    {
        // Arrange
        var client = factory.CreateClient();

        // Act
        await DemoTestSupport.StartDemoAsync(client);

        // Assert
        var userId = (await DemoTestSupport.MeAsync(client)).UserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.Workouts.AnyAsync(w => w.UserId == userId));
            Assert.False(await db.Workouts.AnyAsync(w => w.UserId == userId &&
                (w.Title != null || w.Location != null || w.Notes != null || w.BodyweightKg != null)));
        }
        var privacy = await client.GetFromJsonAsync<AccountPrivacyResponse>("/account/privacy");
        Assert.True(privacy!.RequiresAcknowledgement);
    }
}
