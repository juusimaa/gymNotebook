using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

public class ChangePasswordTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private const string OriginalPassword = "correct-horse-battery-staple";
    private const string NewPassword = "new-correct-horse-battery-staple";

    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueUsername() => $"user-{Guid.NewGuid():N}";

    private async Task<string> RegisterAndGetTokenAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/auth/register", new RegisterRequest(username, password, null));
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private async Task<HttpResponseMessage> ChangePasswordAsync(string token, ChangePasswordRequest request)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password")
        {
            Content = JsonContent.Create(request),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(httpRequest);
    }

    private async Task<HttpStatusCode> MeStatusAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Change_password_with_correct_current_password_returns_new_token()
    {
        var token = await RegisterAndGetTokenAsync(UniqueUsername(), OriginalPassword);

        var response = await ChangePasswordAsync(token, new ChangePasswordRequest(OriginalPassword, NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task Old_token_is_rejected_after_a_password_change()
    {
        var oldToken = await RegisterAndGetTokenAsync(UniqueUsername(), OriginalPassword);

        await ChangePasswordAsync(oldToken, new ChangePasswordRequest(OriginalPassword, NewPassword));

        // This is the end-to-end proof MeTests couldn't give: token_version now advances
        // through the real endpoint, not a direct DB mutation standing in for it.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(oldToken));
    }

    [Fact]
    public async Task New_token_from_the_response_still_works()
    {
        var oldToken = await RegisterAndGetTokenAsync(UniqueUsername(), OriginalPassword);

        var changeResponse = await ChangePasswordAsync(oldToken, new ChangePasswordRequest(OriginalPassword, NewPassword));
        var newToken = (await changeResponse.Content.ReadFromJsonAsync<AuthResponse>())!.Token;

        // The caller who correctly changed their password shouldn't be logged out by
        // their own action (PLAN.md, Auth section) — this is what proves that.
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(newToken));
    }

    [Fact]
    public async Task Change_password_with_wrong_current_password_returns_unauthorized()
    {
        var token = await RegisterAndGetTokenAsync(UniqueUsername(), OriginalPassword);

        var response = await ChangePasswordAsync(token, new ChangePasswordRequest("totally-wrong-password", NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Change_password_with_blank_new_password_returns_bad_request()
    {
        var token = await RegisterAndGetTokenAsync(UniqueUsername(), OriginalPassword);

        var response = await ChangePasswordAsync(token, new ChangePasswordRequest(OriginalPassword, "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Change_password_without_a_token_returns_unauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest(OriginalPassword, NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
