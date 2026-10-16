using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

public static class DemoTestSupport
{
    // Starts a demo through POST /auth/demo, checks it worked, and signs `client` in with
    // the token it got, which is also returned.
    public static async Task<string> StartDemoAsync(HttpClient client, int offset = 180, string? turnstileToken = null)
    {
        var response = await client.PostAsJsonAsync("/auth/demo", new DemoStartRequest(turnstileToken, offset));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return token;
    }

    public static async Task<MeResponse> MeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MeResponse>("/auth/me"))!;

    public static Task<HttpResponseMessage> ResetAsync(HttpClient client, int? offset = 180) =>
        client.PostAsJsonAsync("/account/demo/reset", new DemoResetRequest(offset));

    // Live demos (unexpired) in the host's database.
    public static async Task<int> CountLiveDemosAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.CountAsync(u => u.DemoExpiresAt > DateTimeOffset.UtcNow);
    }

    // Expires one demo (or every demo, with no id) a minute ago, as time passing would.
    public static async Task ExpireAsync(IServiceProvider services, int? userId = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(u => u.DemoExpiresAt != null && (userId == null || u.Id == userId))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DemoExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
}
