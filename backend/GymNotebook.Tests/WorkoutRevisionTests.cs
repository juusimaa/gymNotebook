using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

public class WorkoutRevisionTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> NewTokenAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            DisplayName = "Revision tester",
            Email = $"{Guid.NewGuid():N}@example.test",
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple"),
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement.Clone();

    private async Task<int> CreateAsync(string token)
    {
        var response = await SendAsync(HttpMethod.Post, "/workouts", token,
            new CreateWorkoutRequest(new DateOnly(2026, 10, 8), DateTimeOffset.UtcNow, null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task WriteRoutes_ValidRevision_IncrementAndReturnRevision()
    {
        var token = await NewTokenAsync();
        var id = await CreateAsync(token);

        var patch = await SendAsync(HttpMethod.Patch, $"/workouts/{id}", token,
            new { title = "Session", expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(2, (await BodyAsync(patch)).GetProperty("revision").GetInt32());

        var put = await SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token,
            new { exercises = new[] { new { exerciseName = "Squat", sets = new[] { new { weight = 80m, reps = 5, isWarmup = false } } } }, expectedRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var page = await BodyAsync(put);
        Assert.Equal(3, page.GetProperty("revision").GetInt32());
        var setId = page.GetProperty("exercises")[0].GetProperty("sets")[0].GetProperty("id").GetInt32();

        var post = await SendAsync(HttpMethod.Post, $"/workouts/{id}/sets", token,
            new { exerciseName = "Squat", weight = 90m, reps = 3, isWarmup = false, expectedRevision = 3 });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(4, (await BodyAsync(post)).GetProperty("revision").GetInt32());

        var updateSet = await SendAsync(HttpMethod.Patch, $"/workouts/{id}/sets/{setId}", token,
            new { weight = 85m, reps = 4, isWarmup = false, expectedRevision = 4 });
        Assert.Equal(HttpStatusCode.OK, updateSet.StatusCode);
        Assert.Equal(5, (await BodyAsync(updateSet)).GetProperty("revision").GetInt32());

        var deleteSet = await SendAsync(HttpMethod.Delete, $"/workouts/{id}/sets/{setId}?expectedRevision=5", token);
        Assert.Equal(HttpStatusCode.NoContent, deleteSet.StatusCode);
        var get = await SendAsync(HttpMethod.Get, $"/workouts/{id}", token);
        Assert.Equal(6, (await BodyAsync(get)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ReplaceExercises_StaleRevision_ReturnsConflictWithoutChangingPage()
    {
        var token = await NewTokenAsync();
        var id = await CreateAsync(token);
        var first = await SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token,
            new { exercises = new[] { new { exerciseName = "Squat", sets = new[] { new { weight = 80m, reps = 5, isWarmup = false } } } }, expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token,
            new { exercises = Array.Empty<object>(), expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await BodyAsync(stale);
        Assert.Equal("page_changed", conflict.GetProperty("code").GetString());
        Assert.Equal(2, conflict.GetProperty("revision").GetInt32());

        var get = await SendAsync(HttpMethod.Get, $"/workouts/{id}", token);
        var page = await BodyAsync(get);
        Assert.Equal(2, page.GetProperty("revision").GetInt32());
        Assert.Single(page.GetProperty("exercises").EnumerateArray());
    }

    [Fact]
    public async Task HeadingAndSetWrites_StaleRevision_ReturnConflictWithoutChangingData()
    {
        var token = await NewTokenAsync();
        var id = await CreateAsync(token);
        var put = await SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token,
            new { exercises = new[] { new { exerciseName = "Squat", sets = new[] { new { weight = 80m, reps = 5, isWarmup = false } } } }, expectedRevision = 1 });
        var setId = (await BodyAsync(put)).GetProperty("exercises")[0].GetProperty("sets")[0].GetProperty("id").GetInt32();

        var staleHeading = await SendAsync(HttpMethod.Patch, $"/workouts/{id}", token,
            new { title = "Wrong", expectedRevision = 1 });
        var staleAdd = await SendAsync(HttpMethod.Post, $"/workouts/{id}/sets", token,
            new { exerciseName = "Bench", weight = 90m, reps = 3, isWarmup = false, expectedRevision = 1 });
        var staleSet = await SendAsync(HttpMethod.Patch, $"/workouts/{id}/sets/{setId}", token,
            new { weight = 999m, reps = 1, isWarmup = false, expectedRevision = 1 });
        var staleDelete = await SendAsync(HttpMethod.Delete, $"/workouts/{id}/sets/{setId}?expectedRevision=1", token);

        foreach (var response in new[] { staleHeading, staleAdd, staleSet, staleDelete })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await BodyAsync(response);
            Assert.Equal("page_changed", body.GetProperty("code").GetString());
            Assert.Equal(2, body.GetProperty("revision").GetInt32());
        }

        var get = await SendAsync(HttpMethod.Get, $"/workouts/{id}", token);
        var page = await BodyAsync(get);
        Assert.Equal(2, page.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("title").ValueKind);
        var exercise = Assert.Single(page.GetProperty("exercises").EnumerateArray());
        var set = Assert.Single(exercise.GetProperty("sets").EnumerateArray());
        Assert.Equal(setId, set.GetProperty("id").GetInt32());
        Assert.Equal(80m, set.GetProperty("weight").GetDecimal());
    }

    [Fact]
    public async Task WriteRoutes_OmittedRevision_AcceptedAndIncremented()
    {
        var token = await NewTokenAsync();
        var id = await CreateAsync(token);
        var patch = await SendAsync(HttpMethod.Patch, $"/workouts/{id}", token, new { title = "One" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(2, (await BodyAsync(patch)).GetProperty("revision").GetInt32());
        var put = await SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token, new { exercises = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(3, (await BodyAsync(put)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ReplaceExercises_OtherUsersPage_ReturnsNotFoundForAnyRevision()
    {
        var owner = await NewTokenAsync();
        var other = await NewTokenAsync();
        var id = await CreateAsync(owner);
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", other,
            new { exercises = Array.Empty<object>(), expectedRevision = 999 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceExercises_ConcurrentSameRevision_ExactlyOneSucceeds()
    {
        var token = await NewTokenAsync();
        var id = await CreateAsync(token);
        var first = SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token,
            new { exercises = new[] { new { exerciseName = "Squat", sets = Array.Empty<object>() } }, expectedRevision = 1 });
        var second = SendAsync(HttpMethod.Put, $"/workouts/{id}/exercises", token,
            new { exercises = new[] { new { exerciseName = "Bench", sets = Array.Empty<object>() } }, expectedRevision = 1 });
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var get = await SendAsync(HttpMethod.Get, $"/workouts/{id}", token);
        Assert.Equal(2, (await BodyAsync(get)).GetProperty("revision").GetInt32());
    }
}
