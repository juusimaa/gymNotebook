using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// The "auth" limiter behind a trusted proxy (Program.cs, UseForwardedHeaders). Every test
// fakes the ingress by setting X-Forwarded-For itself. The tests in this class share one
// fixture, and so one set of limiter counters, which is fine because each uses its own
// client addresses (documentation ranges from RFC 5737) and so its own buckets.
public class ForwardedHeadersRateLimitingTests(ForwardedHeadersGymNotebookFactory factory)
    : IClassFixture<ForwardedHeadersGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Login_DifferentForwardedClients_GetSeparateBuckets()
    {
        // Arrange: exhaust the limit (PermitLimit 2) for one client.
        await LoginAs("203.0.113.1");
        await LoginAs("203.0.113.1");
        var exhausted = await LoginAs("203.0.113.1");

        // Act: a different client, as the ingress would report it.
        var other = await LoginAs("203.0.113.2");

        // Assert: the first is limited; the second reaches the handler and gets the
        // ordinary wrong-credentials 401 rather than sharing the first client's bucket.
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task Login_SpoofedLeftmostForwardedFor_StillLimited()
    {
        // Arrange: exhaust the limit for the address the ingress appends.
        await LoginAs("203.0.113.3");
        await LoginAs("203.0.113.3");

        // Act: the client prepends a made-up address. The real ingress keeps it and appends
        // the true caller on the right, which is the only entry ForwardLimit = 1 trusts.
        var response = await LoginAs("198.51.100.9, 203.0.113.3");

        // Assert: still counted against the true caller's bucket.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private Task<HttpResponseMessage> LoginAs(string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "whatever")),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return _client.SendAsync(request);
    }
}

// With the switch off (the default, and every local run), X-Forwarded-For must be ignored:
// otherwise any caller could pick a fresh bucket per request. Its own class, and so its own
// fixture instance, because it exhausts TestServer's single shared bucket (see
// RateLimitingTests.cs).
public class ForwardedHeadersDisabledRateLimitingTests(RateLimitedGymNotebookFactory factory)
    : IClassFixture<RateLimitedGymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Login_ForwardedForWithSwitchOff_IsIgnored()
    {
        // Arrange: two requests claiming two different clients.
        await LoginAs("203.0.113.10");
        await LoginAs("203.0.113.11");

        // Act: a third, claiming yet another client.
        var response = await LoginAs("203.0.113.12");

        // Assert: all three drew from the one connection-address bucket.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private Task<HttpResponseMessage> LoginAs(string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "whatever")),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return _client.SendAsync(request);
    }
}
