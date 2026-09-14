using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// Login and register share one "auth" rate-limit policy partitioned by client IP (see
// Program.cs) — every request from the same IP draws from the same bucket regardless of
// which of the two endpoints it hits. That means these two cases can't live in the same
// test class: TestServer gives every request the same synthetic IP, so if both tests ran
// against one shared RateLimitedGymNotebookFactory instance, whichever runs first would
// exhaust the shared budget and the other would start already rate-limited. Each gets its
// own factory instance (xUnit creates one per test class) so neither can affect the other.

public class LoginRateLimitingTests(RateLimitedGymNotebookFactory factory) : IClassFixture<RateLimitedGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Exceeding_the_login_rate_limit_returns_too_many_requests()
    {
        var request = new LoginRequest("nobody", "whatever");

        // PermitLimit is 2 for this fixture — the first two requests consume the window's
        // allowance regardless of their own outcome, the third must be rejected by the
        // limiter itself before /auth/login's handler even runs.
        await _client.PostAsJsonAsync("/auth/login", request);
        await _client.PostAsJsonAsync("/auth/login", request);
        var response = await _client.PostAsJsonAsync("/auth/login", request);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}

public class RegisterRateLimitingTests(RateLimitedGymNotebookFactory factory) : IClassFixture<RateLimitedGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Exceeding_the_register_rate_limit_returns_too_many_requests()
    {
        // A local function rather than a shared request object because each attempt needs
        // a fresh username — a repeat would 409, which the limiter would still count, but
        // the test should exceed the limit with requests that would otherwise succeed.
        Task<HttpResponseMessage> Register() => _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest($"user-{Guid.NewGuid():N}", "correct-horse-battery-staple", null));

        await Register();
        await Register();
        var response = await Register();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
