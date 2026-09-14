using System.Net;

namespace GymNotebook.Tests;

// CORS is enforced by the browser, not the server: the API answers every request the same
// way and only *adds* Access-Control-* headers when the Origin is on the allow-list. So a
// disallowed origin is tested by the header being absent on a 200, never by a 403 — and a
// wrong middleware order shows up as the preflight failing, since an OPTIONS carries no
// bearer token and must be answered before authentication or the rate limiter see it.
// The allowed-origin cases use /health so the class never touches the auth rate-limit
// bucket the fixture shares with every other test class.
public class CorsTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private const string AllowOriginHeader = "Access-Control-Allow-Origin";

    private readonly HttpClient _client = factory.CreateClient();

    // A preflight is what the browser sends before a POST with a JSON body: OPTIONS with
    // the method and headers it intends to use. 204 with the origin echoed back is the
    // "yes" that lets the real request follow. /auth/login is deliberate — it's the first
    // cross-origin request the frontend will ever make, and it sits behind the rate limiter
    // that a wrongly ordered UseCors would let the preflight fall into.
    [Fact]
    public async Task Preflight_from_allowed_origin_is_accepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/auth/login");
        request.Headers.Add("Origin", GymNotebookFactory.AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(GymNotebookFactory.AllowedOrigin, GetSingleHeader(response, AllowOriginHeader));
        Assert.Contains("POST", GetSingleHeader(response, "Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task Request_from_allowed_origin_gets_allow_origin_header()
    {
        var response = await _client.SendAsync(GetHealthFrom(GymNotebookFactory.AllowedOrigin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GymNotebookFactory.AllowedOrigin, GetSingleHeader(response, AllowOriginHeader));
    }

    // The fixture configures two origins separated by ", " — this one passing is what
    // proves Program.cs splits on the comma and trims the space, not just the first case.
    [Fact]
    public async Task Request_from_second_allowed_origin_gets_allow_origin_header()
    {
        var response = await _client.SendAsync(GetHealthFrom(GymNotebookFactory.SecondAllowedOrigin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GymNotebookFactory.SecondAllowedOrigin, GetSingleHeader(response, AllowOriginHeader));
    }

    // 200, not 403: the server still serves the request, it just withholds the header, and
    // the browser is what refuses to hand the response to the page.
    [Fact]
    public async Task Request_from_unknown_origin_gets_no_allow_origin_header()
    {
        var response = await _client.SendAsync(GetHealthFrom("http://evil.example"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(AllowOriginHeader));
    }

    private static HttpRequestMessage GetHealthFrom(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", origin);
        return request;
    }

    // Single() rather than First(): a CORS header appearing twice is itself a bug (two
    // middlewares adding it), and the browser treats a duplicated Allow-Origin as invalid.
    private static string GetSingleHeader(HttpResponseMessage response, string name) =>
        response.Headers.GetValues(name).Single();
}
