using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// The "arrange" steps every test class that touches auth would otherwise repeat: get a
// user with a token, then call something as that user. Extension methods so the call
// sites read like the plain HttpClient calls next to them.
public static class HttpClientAuthExtensions
{
    // Registers a user and returns their token. Throws if registration fails, so a broken
    // arrange step fails on this line rather than as a null token three lines later.
    // Tests that exercise /auth/register itself still post to it directly — this is for
    // tests that merely need a user to exist.
    public static async Task<string> RegisterAsync(
        this HttpClient client,
        string username,
        string password = TestUsers.DefaultPassword)
    {
        var response = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(username, password, null));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    // HttpClient's GetAsync and PostAsJsonAsync can't take per-request headers, so an
    // authenticated call needs a hand-built HttpRequestMessage with the bearer token on it.
    public static Task<HttpResponseMessage> GetAsync(this HttpClient client, string url, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(this HttpClient client, string url, T body, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
