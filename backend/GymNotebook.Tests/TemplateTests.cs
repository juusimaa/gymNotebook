using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/006 user story 4 (backend): named per-user templates, start a session from one,
// save a page as one. Copies are never linked, so a change on either side never reaches
// the other (research R8). The consent rule for a started page's title needs the privacy
// flag on, so those tests sit in OptionalDetailsConsentTests with that factory.
public class TemplateTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // Seeded rather than registered, for the same rate-limit reason as SetsTests.
    private async Task<(string Token, int UserId)> RegisterAndGetUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new User
        {
            DisplayName = "Test user",
            Email = $"{Guid.NewGuid():N}@example.test",
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple"),
            TokenVersion = 0,
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);
        return (token, user.Id);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string url, string token, object? body = null)
    {
        var response = await SendAsync(method, url, token, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {url} returned {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(code, error!.Code);
    }

    // "Push A": a warm-up and a working set of bench, then one set of overhead press.
    private static SaveTemplateRequest PushA(string name = "Push A", decimal benchWeight = 80m) => new(name,
    [
        new("Bench Press", [new(60m, 8, true), new(benchWeight, 5, false)]),
        new("Overhead Press", [new(40m, 8, false)]),
    ]);

    private Task<TemplateDetailResponse> CreateTemplateAsync(string token, SaveTemplateRequest request) =>
        ReadAsync<TemplateDetailResponse>(HttpMethod.Post, "/templates", token, request);

    // The local day and the moment of the tap, as the client sends them.
    private static StartFromTemplateRequest StartNow =>
        new(new DateOnly(2026, 10, 15), new DateTimeOffset(2026, 10, 15, 17, 30, 0, TimeSpan.FromHours(3)));

    private Task<WorkoutDetailResponse> StartAsync(string token, int templateId) =>
        ReadAsync<WorkoutDetailResponse>(HttpMethod.Post, $"/templates/{templateId}/start", token, StartNow);

    [Fact]
    public async Task CreateTemplate_Valid_Returns201()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();

        // Act
        var response = await SendAsync(HttpMethod.Post, "/templates", token, PushA());

        // Assert: the name as typed, blocks and sets in order, numbered from the array.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TemplateDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal($"/templates/{body.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Push A", body.Name);
        Assert.Equal(["Bench Press", "Overhead Press"], body.Exercises.Select(e => e.ExerciseName));
        var bench = body.Exercises[0].Sets;
        Assert.Equal([1, 2], bench.Select(s => s.SetNumber));
        Assert.Equal((60m, 8, true), (bench[0].Weight!.Value, bench[0].Reps, bench[0].IsWarmup));
        Assert.Equal((80m, 5, false), (bench[1].Weight!.Value, bench[1].Reps, bench[1].IsWarmup));
    }

    [Fact]
    public async Task CreateTemplate_NameDiffersOnlyByCaseAndSpaces_Returns409AndWritesNothing()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        await CreateTemplateAsync(token, PushA());

        // Act: the same name after normalization, with an exercise that doesn't exist yet.
        var response = await SendAsync(HttpMethod.Post, "/templates", token,
            new SaveTemplateRequest("  push   A ", [new("Cable Fly", [new(15m, 12, false)])]));

        // Assert: refused, and the request left nothing behind, not even its new exercise.
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "template_name_taken");
        var list = await ReadAsync<List<TemplateSummaryResponse>>(HttpMethod.Get, "/templates", token);
        Assert.Equal(["Push A"], list.Select(t => t.Name));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Exercises.AnyAsync(e => e.UserId == userId && e.NormalizedName == "cable fly"));
    }

    [Fact]
    public async Task CreateTemplate_SameNameOtherUser_Returns201()
    {
        // Arrange
        var (tokenA, _) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        await CreateTemplateAsync(tokenA, PushA());

        // Act
        var response = await SendAsync(HttpMethod.Post, "/templates", tokenB, PushA());

        // Assert: names are unique per user only.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RenameTemplate_OntoTakenName_Returns409()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        await CreateTemplateAsync(token, PushA());
        var pullB = await CreateTemplateAsync(token, new SaveTemplateRequest("Pull B", [new("Barbell Row", [new(70m, 8, false)])]));

        // Act
        var response = await SendAsync(HttpMethod.Put, $"/templates/{pullB.Id}", token, PushA("PUSH A"));

        // Assert: refused, and Pull B kept its name and blocks.
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "template_name_taken");
        var stored = await ReadAsync<TemplateDetailResponse>(HttpMethod.Get, $"/templates/{pullB.Id}", token);
        Assert.Equal("Pull B", stored.Name);
        Assert.Equal(["Barbell Row"], stored.Exercises.Select(e => e.ExerciseName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateTemplate_BlankName_Returns400(string? name)
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();

        // Act
        var response = await SendAsync(HttpMethod.Post, "/templates", token, PushA() with { Name = name });

        // Assert
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("  ", 5)]
    [InlineData("Bench Press", 0)]
    public async Task CreateTemplate_BlankExerciseOrNoReps_Returns400(string exerciseName, int reps)
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();

        // Act
        var response = await SendAsync(HttpMethod.Post, "/templates", token,
            new SaveTemplateRequest("Push A", [new(exerciseName, [new(60m, reps, false)])]));

        // Assert
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task ListTemplates_OrderedByName_OnlyCallers()
    {
        // Arrange: three of A's templates written out of order, and one of B's.
        var (tokenA, _) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        await CreateTemplateAsync(tokenA, PushA());
        await CreateTemplateAsync(tokenA, new SaveTemplateRequest("Legs", [new("Back Squat", [new(100m, 5, false)])]));
        await CreateTemplateAsync(tokenA, new SaveTemplateRequest("Pull B", []));
        await CreateTemplateAsync(tokenB, new SaveTemplateRequest("Arms", []));

        // Act
        var list = await ReadAsync<List<TemplateSummaryResponse>>(HttpMethod.Get, "/templates", tokenA);

        // Assert: A's only, by name, each with its names and counts.
        Assert.Equal(["Legs", "Pull B", "Push A"], list.Select(t => t.Name));
        var push = list.Single(t => t.Name == "Push A");
        Assert.Equal(["Bench Press", "Overhead Press"], push.ExerciseNames);
        Assert.Equal((2, 3), (push.ExerciseCount, push.SetCount));
        Assert.Equal((0, 0), (list[1].ExerciseCount, list[1].SetCount));
    }

    [Fact]
    public async Task GetTemplate_OtherUser_Returns404()
    {
        // Arrange
        var (tokenA, _) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(tokenA, PushA());

        // Act: every route that takes a template id, as B.
        var get = await SendAsync(HttpMethod.Get, $"/templates/{template.Id}", tokenB);
        var put = await SendAsync(HttpMethod.Put, $"/templates/{template.Id}", tokenB, PushA("Mine now"));
        var start = await SendAsync(HttpMethod.Post, $"/templates/{template.Id}/start", tokenB, StartNow);
        var delete = await SendAsync(HttpMethod.Delete, $"/templates/{template.Id}", tokenB);

        // Assert: 404, never 403, and A's template untouched.
        Assert.Equal(
            [HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound],
            [get.StatusCode, put.StatusCode, start.StatusCode, delete.StatusCode]);
        var stored = await ReadAsync<TemplateDetailResponse>(HttpMethod.Get, $"/templates/{template.Id}", tokenA);
        Assert.Equal("Push A", stored.Name);
    }

    [Fact]
    public async Task PutTemplate_ReplacesBlocks()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, PushA());

        // Act: a rename and a whole new block list.
        var response = await SendAsync(HttpMethod.Put, $"/templates/{template.Id}", token,
            new SaveTemplateRequest("Push A (light)", [new("Dips", [new(null, 10, false), new(null, 8, false)])]));

        // Assert: the response and a fresh read both hold only the new blocks.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadAsync<TemplateDetailResponse>(HttpMethod.Get, $"/templates/{template.Id}", token);
        Assert.Equal("Push A (light)", stored.Name);
        var dips = Assert.Single(stored.Exercises);
        Assert.Equal("Dips", dips.ExerciseName);
        Assert.Equal([(1, 10), (2, 8)], dips.Sets.Select(s => (s.SetNumber, s.Reps)));
        Assert.All(dips.Sets, s => Assert.Null(s.Weight));
    }

    [Fact]
    public async Task DeleteTemplate_LeavesStartedPages()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, PushA());
        var page = await StartAsync(token, template.Id);

        // Act
        var response = await SendAsync(HttpMethod.Delete, $"/templates/{template.Id}", token);

        // Assert: the template is gone, the page started from it is whole.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/templates/{template.Id}", token)).StatusCode);
        var stored = await ReadAsync<WorkoutDetailResponse>(HttpMethod.Get, $"/workouts/{page.Id}", token);
        Assert.Equal(3, stored.Exercises.Sum(e => e.Sets.Count));
    }

    [Fact]
    public async Task Start_CopiesBlocksAsPlannedInProgress()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, PushA());

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/templates/{template.Id}/start", token, StartNow);

        // Assert: an in-progress page dated and started as sent (the start in UTC), titled
        // after the template, with every set copied as planned.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>())!;
        Assert.Equal($"/workouts/{page.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(StartNow.Date, page.Date);
        Assert.Equal(StartNow.StartedAt!.Value.ToUniversalTime(), page.StartedAt);
        Assert.Null(page.EndedAt);
        Assert.Equal("Push A", page.Title);
        Assert.Equal(["Bench Press", "Overhead Press"], page.Exercises.Select(e => e.ExerciseName));
        Assert.Equal(
            template.Exercises.SelectMany(e => e.Sets.Select(s => (s.SetNumber, s.Weight, s.Reps, s.IsWarmup))),
            page.Exercises.SelectMany(e => e.Sets.Select(s => (s.SetNumber, s.Weight, s.Reps, s.IsWarmup))));
        Assert.All(page.Exercises.SelectMany(e => e.Sets), s => Assert.Equal("planned", s.Status));
        // Listed with the history, in progress, with nothing done yet.
        var history = await ReadAsync<List<WorkoutSummaryResponse>>(HttpMethod.Get, "/workouts", token);
        var row = Assert.Single(history, w => w.Id == page.Id);
        Assert.Equal((0, 3), (row.SetCount, row.NotDoneSetCount));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Start_MissingDateOrStart_Returns400AndWritesNothing(bool sendDate, bool sendStartedAt)
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, PushA());
        var body = new StartFromTemplateRequest(
            sendDate ? StartNow.Date : null,
            sendStartedAt ? StartNow.StartedAt : null);

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/templates/{template.Id}/start", token, body);

        // Assert
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await ReadAsync<List<WorkoutSummaryResponse>>(HttpMethod.Get, "/workouts", token));
    }

    [Fact]
    public async Task Start_ThenEditPage_TemplateUnchanged()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, PushA());
        var page = await StartAsync(token, template.Id);

        // Act: a heavier bench on the page.
        var put = await SendAsync(HttpMethod.Put, $"/workouts/{page.Id}/exercises", token,
            new PutWorkoutExercisesRequest([new("Bench Press", [new(60m, 8, true), new(85m, 5, false)])]));

        // Assert
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stored = await ReadAsync<TemplateDetailResponse>(HttpMethod.Get, $"/templates/{template.Id}", token);
        Assert.Equal(80m, stored.Exercises[0].Sets[1].Weight);
        Assert.Equal(2, stored.Exercises.Count);
    }

    [Fact]
    public async Task EditTemplate_AfterStart_PageUnchanged()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, PushA());
        var page = await StartAsync(token, template.Id);

        // Act: a heavier bench in the template.
        var put = await SendAsync(HttpMethod.Put, $"/templates/{template.Id}", token, PushA(benchWeight: 85m));

        // Assert
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stored = await ReadAsync<WorkoutDetailResponse>(HttpMethod.Get, $"/workouts/{page.Id}", token);
        Assert.Equal(80m, stored.Exercises[0].Sets[1].Weight);
    }

    [Fact]
    public async Task SaveAsTemplate_FinishedPageWithSkipped_CopiesAllSets()
    {
        // Arrange: a finished page with a done warm-up, a done working set and a skipped one.
        var (token, userId) = await RegisterAndGetUserAsync();
        int pageId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var exercise = db.Exercises.Add(new Exercise { UserId = userId, Name = "Back Squat", NormalizedName = "back squat" }).Entity;
            await db.SaveChangesAsync();
            var block = new WorkoutExercise { ExerciseId = exercise.Id, Position = 0 };
            block.SetEntries.Add(new SetEntry { SetNumber = 1, Weight = 60m, Reps = 8, IsWarmup = true, Status = SetStatus.Done });
            block.SetEntries.Add(new SetEntry { SetNumber = 2, Weight = 100m, Reps = 5, Status = SetStatus.Done });
            block.SetEntries.Add(new SetEntry { SetNumber = 3, Weight = 100m, Reps = 5, Status = SetStatus.Skipped });
            var workout = new Workout
            {
                UserId = userId,
                Date = new DateOnly(2026, 10, 1),
                StartedAt = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero),
                EndedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
            };
            workout.WorkoutExercises.Add(block);
            db.Workouts.Add(workout);
            await db.SaveChangesAsync();
            pageId = workout.Id;
        }

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/workouts/{pageId}/template", token, new SaveAsTemplateRequest("Legs"));

        // Assert: all three sets, the skipped one included.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var template = (await response.Content.ReadFromJsonAsync<TemplateDetailResponse>())!;
        Assert.Equal("Legs", template.Name);
        var squat = Assert.Single(template.Exercises);
        Assert.Equal(
            [(1, 60m, 8, true), (2, 100m, 5, false), (3, 100m, 5, false)],
            squat.Sets.Select(s => (s.SetNumber, s.Weight!.Value, s.Reps, s.IsWarmup)));
    }

    [Fact]
    public async Task SaveAsTemplate_OtherUsersPage_Returns404()
    {
        // Arrange
        var (tokenA, _) = await RegisterAndGetUserAsync();
        var (tokenB, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(tokenA, PushA());
        var page = await StartAsync(tokenA, template.Id);

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/workouts/{page.Id}/template", tokenB, new SaveAsTemplateRequest("Stolen"));

        // Assert: 404, and B has no template.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ReadAsync<List<TemplateSummaryResponse>>(HttpMethod.Get, "/templates", tokenB));
    }

    [Fact]
    public async Task MergeExercise_UsedInTemplate_RepointsTemplateBlocks()
    {
        // Arrange: one template using two exercises that are really the same lift.
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, new SaveTemplateRequest("Push A",
        [
            new("Bench", [new(80m, 5, false)]),
            new("Bench Press", [new(80m, 5, false)]),
        ]));
        var survivorId = template.Exercises[0].ExerciseId;

        // Act: renaming "Bench" onto "Bench Press" merges the two (PLAN.md).
        var response = await SendAsync(HttpMethod.Patch, $"/exercises/{survivorId}", token, new UpdateExerciseRequest("Bench Press", null));

        // Assert: both template blocks now point at the surviving exercise.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadAsync<TemplateDetailResponse>(HttpMethod.Get, $"/templates/{template.Id}", token);
        Assert.All(stored.Exercises, e => Assert.Equal((survivorId, "Bench Press"), (e.ExerciseId, e.ExerciseName)));
    }

    [Fact]
    public async Task Templates_NeverAffectExerciseSessionCount()
    {
        // Arrange: an exercise that exists only in a template and the page started from it,
        // where no set has been ticked done yet.
        var (token, _) = await RegisterAndGetUserAsync();
        var template = await CreateTemplateAsync(token, new SaveTemplateRequest("Legs", [new("Front Squat", [new(70m, 5, false)])]));
        await StartAsync(token, template.Id);

        // Act
        var exercises = await ReadAsync<List<ExerciseResponse>>(HttpMethod.Get, "/exercises?search=front", token);

        // Assert: the exercise exists (typed into the template), but nothing was lifted.
        var frontSquat = Assert.Single(exercises);
        Assert.Equal(0, frontSquat.SessionCount);
        Assert.Null(frontSquat.LastSet);
    }
}
