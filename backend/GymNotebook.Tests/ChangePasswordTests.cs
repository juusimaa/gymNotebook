using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;

namespace GymNotebook.Tests;

// /auth/change-password end to end, including the part MeTests could only simulate:
// that the endpoint's token_version bump really does revoke the old token.
public class ChangePasswordTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private const string NewPassword = "new-correct-horse-battery-staple";

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Change_password_with_correct_current_password_returns_new_token()
    {
        var token = await _client.RegisterAsync(TestUsers.UniqueUsername());

        var response = await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest(TestUsers.DefaultPassword, NewPassword),
            token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task Old_token_is_rejected_after_a_password_change()
    {
        var oldToken = await _client.RegisterAsync(TestUsers.UniqueUsername());

        await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest(TestUsers.DefaultPassword, NewPassword),
            oldToken);

        // /auth/me as the oracle: "is this token still accepted" is exactly the question,
        // and that endpoint exists to answer it. This is the end-to-end proof MeTests
        // couldn't give: token_version now advances through the real endpoint, not a
        // direct DB mutation standing in for it.
        var me = await _client.GetAsync("/auth/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task New_token_from_the_response_still_works()
    {
        var oldToken = await _client.RegisterAsync(TestUsers.UniqueUsername());

        var changeResponse = await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest(TestUsers.DefaultPassword, NewPassword),
            oldToken);
        var newToken = (await changeResponse.Content.ReadFromJsonAsync<AuthResponse>())!.Token;

        // The caller who correctly changed their password shouldn't be logged out by
        // their own action (PLAN.md, Auth section) — this is what proves that.
        var me = await _client.GetAsync("/auth/me", newToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Change_password_with_wrong_current_password_returns_unauthorized()
    {
        var token = await _client.RegisterAsync(TestUsers.UniqueUsername());

        var response = await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest("totally-wrong-password", NewPassword),
            token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Change_password_with_blank_new_password_returns_bad_request()
    {
        var token = await _client.RegisterAsync(TestUsers.UniqueUsername());

        var response = await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest(TestUsers.DefaultPassword, "   "),
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Change_password_without_a_token_returns_unauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/auth/change-password",
            new ChangePasswordRequest(TestUsers.DefaultPassword, NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
