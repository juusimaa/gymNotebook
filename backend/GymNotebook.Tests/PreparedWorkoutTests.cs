using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GymNotebook.Tests;

// specs/006 prepared pages (user stories 1 and 2, backend): a page written before the gym
// has no date or start, lives on its own list rather than in history, holds only planned
// sets, and gets its date and start from POST /workouts/{id}/start. Pages and sets are
// seeded straight through AppDbContext, as in SetStatusTests, so each test starts from an
// exact state.
public class PreparedWorkoutTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private static readonly DateOnly _startDate = new(2026, 10, 16);
    private static readonly DateTimeOffset _startedAt = new(2026, 10, 16, 7, 12, 0, TimeSpan.FromHours(3));

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

    // A prepared page (no date, no start) holding one "Back Squat" block with the given
    // number of planned sets. createdAt is explicit so list-order tests don't depend on
    // how fast two inserts follow each other.
    private async Task<Workout> SeedPreparedAsync(int userId, int plannedSets = 0, string? title = null, DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var workout = new Workout { UserId = userId, Title = title, CreatedAt = createdAt ?? DateTimeOffset.UtcNow };
        if (plannedSets > 0)
        {
            var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.UserId == userId && e.NormalizedName == "back squat")
                ?? db.Exercises.Add(new Exercise { UserId = userId, Name = "Back Squat", NormalizedName = "back squat" }).Entity;
            await db.SaveChangesAsync();

            var block = new WorkoutExercise { ExerciseId = exercise.Id, Position = 0 };
            for (var setNumber = 1; setNumber <= plannedSets; setNumber++)
            {
                block.SetEntries.Add(new SetEntry { SetNumber = setNumber, Reps = 5, Weight = 100m, Status = SetStatus.Planned });
            }
            workout.WorkoutExercises.Add(block);
        }
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();
        return workout;
    }

    private async Task<Workout> SeedStartedAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workout = new Workout
        {
            UserId = userId,
            Date = new DateOnly(2026, 1, 1),
            StartedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero),
        };
        db.Workouts.Add(workout);
        await db.SaveChangesAsync();
        return workout;
    }

    private async Task<Workout?> ReadWorkoutAsync(int workoutId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Workouts.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workoutId);
    }

    private async Task<List<SetStatus>> ReadStatusesAsync(int workoutId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await (
            from se in db.SetEntries.AsNoTracking()
            join we in db.WorkoutExercises.AsNoTracking() on se.WorkoutExerciseId equals we.Id
            where we.WorkoutId == workoutId
            orderby we.Position, se.SetNumber
            select se.Status)
            .ToListAsync();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> StartAsync(int workoutId, string token, int? expectedRevision = null) =>
        SendAsync(HttpMethod.Post, $"/workouts/{workoutId}/start", token,
            new StartWorkoutRequest(_startDate, _startedAt, expectedRevision));

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(code, error!.Code);
    }

    [Fact]
    public async Task CreatePrepared_Valid_Returns201WithoutDateOrStart()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();

        // Act
        var response = await SendAsync(HttpMethod.Post, "/workouts/prepared", token,
            new CreatePreparedWorkoutRequest("Push A", null, null, null));

        // Assert: the response and the stored row are both prepared.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.NotNull(body);
        Assert.Null(body.Date);
        Assert.Null(body.StartedAt);
        Assert.Null(body.EndedAt);
        Assert.Equal(1, body.Revision);
        Assert.Equal("Push A", body.Title);
        Assert.Empty(body.Exercises);

        var stored = await ReadWorkoutAsync(body.Id);
        Assert.Equal(userId, stored!.UserId);
        Assert.Null(stored.Date);
        Assert.Null(stored.StartedAt);
    }

    [Fact]
    public async Task ListPrepared_ReturnsOnlyCallersPreparedNewestFirst()
    {
        // Arrange: the older plan is inserted last, so id order and createdAt order differ.
        var (token, userId) = await RegisterAndGetUserAsync();
        var (_, otherUserId) = await RegisterAndGetUserAsync();
        var newer = await SeedPreparedAsync(userId, plannedSets: 3, title: "Push A", createdAt: new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero));
        var older = await SeedPreparedAsync(userId, title: "Pull A", createdAt: new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero));
        await SeedStartedAsync(userId);
        await SeedPreparedAsync(otherUserId, plannedSets: 1);

        // Act
        var response = await SendAsync(HttpMethod.Get, "/workouts/prepared", token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<PreparedWorkoutSummaryResponse>>();
        Assert.Equal([newer.Id, older.Id], rows!.Select(r => r.Id));
        Assert.Equal("Push A", rows[0].Title);
        Assert.Equal(["Back Squat"], rows[0].ExerciseNames);
        Assert.Equal(1, rows[0].ExerciseCount);
        Assert.Equal(3, rows[0].SetCount);
        Assert.Equal(0, rows[1].ExerciseCount);
        Assert.Equal(0, rows[1].SetCount);
    }

    [Fact]
    public async Task ListWorkouts_ExcludesPreparedPages()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var started = await SeedStartedAsync(userId);
        await SeedPreparedAsync(userId, plannedSets: 2);

        // Act
        var response = await SendAsync(HttpMethod.Get, "/workouts", token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<WorkoutSummaryResponse>>();
        Assert.Equal([started.Id], rows!.Select(r => r.Id));
    }

    [Fact]
    public async Task ListWorkouts_PreparedIdAsBefore_Returns400()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        await SeedStartedAsync(userId);
        var prepared = await SeedPreparedAsync(userId);

        // Act
        var response = await SendAsync(HttpMethod.Get, $"/workouts?before={prepared.Id}", token);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutExercises_OnPreparedPage_SavesPlanned()
    {
        // Arrange
        var (token, _) = await RegisterAndGetUserAsync();
        var created = await SendAsync(HttpMethod.Post, "/workouts/prepared", token,
            new CreatePreparedWorkoutRequest(null, null, null, null));
        var page = await created.Content.ReadFromJsonAsync<WorkoutDetailResponse>();

        // Act: no status sent, as the editor sends today; the page state supplies it.
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{page!.Id}/exercises", token,
            new PutWorkoutExercisesRequest([new PutWorkoutExerciseInput("Back Squat",
                [new PutSetInput(100m, 5, false), new PutSetInput(100m, 5, false, "planned")])]));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Planned, SetStatus.Planned], await ReadStatusesAsync(page.Id));
    }

    [Fact]
    public async Task PutExercises_DoneOnPreparedPage_Returns400()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId);

        // Act
        var response = await SendAsync(HttpMethod.Put, $"/workouts/{prepared.Id}/exercises", token,
            new PutWorkoutExercisesRequest([new PutWorkoutExerciseInput("Back Squat", [new PutSetInput(100m, 5, false, "done")])]));

        // Assert: refused, and nothing was written.
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_set_status");
        Assert.Empty(await ReadStatusesAsync(prepared.Id));
    }

    [Theory]
    [InlineData("""{ "startedAt": "2026-10-16T07:12:00Z" }""")]
    [InlineData("""{ "date": "2026-10-16" }""")]
    [InlineData("""{ "endedAt": "2026-10-16T08:00:00Z" }""")]
    public async Task PatchPrepared_StartedAt_Returns400NotStarted(string json)
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/workouts/{prepared.Id}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        // Assert: still prepared, revision untouched.
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "not_started");
        var stored = await ReadWorkoutAsync(prepared.Id);
        Assert.Null(stored!.StartedAt);
        Assert.Null(stored.Date);
        Assert.Null(stored.EndedAt);
        Assert.Equal(prepared.Revision, stored.Revision);
    }

    [Fact]
    public async Task PatchPrepared_HeadingOnly_Returns200()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId);

        // Act
        var response = await SendAsync(HttpMethod.Patch, $"/workouts/{prepared.Id}", token, new { title = "Legs" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal("Legs", body!.Title);
        Assert.Null(body.StartedAt);
    }

    [Fact]
    public async Task GetPrepared_OtherUser_Returns404()
    {
        // Arrange
        var (_, ownerId) = await RegisterAndGetUserAsync();
        var (otherToken, _) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(ownerId, plannedSets: 1);

        // Act
        var response = await SendAsync(HttpMethod.Get, $"/workouts/{prepared.Id}", otherToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrepared_Owner_Returns204()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId, plannedSets: 2);

        // Act
        var response = await SendAsync(HttpMethod.Delete, $"/workouts/{prepared.Id}", token);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await ReadWorkoutAsync(prepared.Id));
        Assert.Empty(await ReadStatusesAsync(prepared.Id));
    }

    [Fact]
    public async Task ListExercises_PreparedPage_NotCounted()
    {
        // Arrange: the exercise exists only through a plan.
        var (token, userId) = await RegisterAndGetUserAsync();
        await SeedPreparedAsync(userId, plannedSets: 3);

        // Act
        var response = await SendAsync(HttpMethod.Get, "/exercises", token);

        // Assert: listed, so autocomplete finds it, but never trained.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exercise = Assert.Single((await response.Content.ReadFromJsonAsync<List<ExerciseResponse>>())!);
        Assert.Equal(0, exercise.SessionCount);
        Assert.Null(exercise.LastSet);
        Assert.Null(exercise.FirstSet);
    }

    [Fact]
    public async Task Database_DateWithoutStart_ViolatesCheck()
    {
        // Arrange: a write that bypasses the API, which the database alone must refuse.
        var (_, userId) = await RegisterAndGetUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Workouts.Add(new Workout { UserId = userId, Date = new DateOnly(2026, 10, 16) });

        // Act
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // Assert
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("ck_workouts_started_pair", postgres.ConstraintName);
    }

    [Fact]
    public async Task Start_Prepared_SetsDateAndStartKeepsPlanned()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId, plannedSets: 3);

        // Act
        var response = await StartAsync(prepared.Id, token, expectedRevision: prepared.Revision);

        // Assert: in progress with the client's date and start (stored as UTC), the
        // revision bumped, and every set still waiting to be ticked off.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        Assert.Equal(_startDate, body!.Date);
        Assert.Equal(_startedAt, body.StartedAt);
        Assert.Null(body.EndedAt);
        Assert.Equal(prepared.Revision + 1, body.Revision);
        Assert.All(body.Exercises.SelectMany(e => e.Sets), set => Assert.Equal("planned", set.Status));

        var stored = await ReadWorkoutAsync(prepared.Id);
        Assert.Equal(_startDate, stored!.Date);
        Assert.Equal(_startedAt, stored.StartedAt);
        Assert.Equal(TimeSpan.Zero, stored.StartedAt!.Value.Offset);
        Assert.Equal([SetStatus.Planned, SetStatus.Planned, SetStatus.Planned], await ReadStatusesAsync(prepared.Id));
    }

    [Fact]
    public async Task Start_Twice_Returns409AlreadyStarted()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId, plannedSets: 1);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(prepared.Id, token)).StatusCode);
        var afterFirst = await ReadWorkoutAsync(prepared.Id);

        // Act: a second start, as from another device, with another start time.
        var response = await SendAsync(HttpMethod.Post, $"/workouts/{prepared.Id}/start", token,
            new StartWorkoutRequest(_startDate.AddDays(1), _startedAt.AddDays(1), null));

        // Assert: the first start stands, revision included.
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "already_started");
        var stored = await ReadWorkoutAsync(prepared.Id);
        Assert.Equal(afterFirst!.StartedAt, stored!.StartedAt);
        Assert.Equal(afterFirst.Date, stored.Date);
        Assert.Equal(afterFirst.Revision, stored.Revision);
    }

    [Fact]
    public async Task Start_StaleRevision_Returns409PageChanged()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId);

        // Act
        var response = await StartAsync(prepared.Id, token, expectedRevision: prepared.Revision + 5);

        // Assert: still prepared.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = await response.Content.ReadFromJsonAsync<PageChangedResponse>();
        Assert.Equal("page_changed", conflict!.Code);
        Assert.Equal(prepared.Revision, conflict.Revision);
        Assert.Null((await ReadWorkoutAsync(prepared.Id))!.StartedAt);
    }

    [Fact]
    public async Task Start_OtherUser_Returns404()
    {
        // Arrange
        var (_, ownerId) = await RegisterAndGetUserAsync();
        var (otherToken, _) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(ownerId);

        // Act
        var response = await StartAsync(prepared.Id, otherToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await ReadWorkoutAsync(prepared.Id))!.StartedAt);
    }

    [Fact]
    public async Task Start_MissingStartedAt_Returns400()
    {
        // Arrange
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId);

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/workouts/{prepared.Id}/start", token,
            new StartWorkoutRequest(_startDate, null, null));

        // Assert
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        var stored = await ReadWorkoutAsync(prepared.Id);
        Assert.Null(stored!.StartedAt);
        Assert.Equal(prepared.Revision, stored.Revision);
    }

    [Fact]
    public async Task StartThenFinish_UnmarkedSets_BecomeSkipped()
    {
        // Arrange: start, then tick the first of three sets off.
        var (token, userId) = await RegisterAndGetUserAsync();
        var prepared = await SeedPreparedAsync(userId, plannedSets: 3);
        var started = await (await StartAsync(prepared.Id, token)).Content.ReadFromJsonAsync<WorkoutDetailResponse>();
        var firstSetId = started!.Exercises.Single().Sets.First().Id;
        var ticked = await SendAsync(HttpMethod.Patch, $"/workouts/{prepared.Id}/sets/{firstSetId}", token,
            new UpdateSetRequest(100m, 5, false, Status: "done"));
        Assert.Equal(HttpStatusCode.OK, ticked.StatusCode);

        // Act
        var response = await SendAsync(HttpMethod.Patch, $"/workouts/{prepared.Id}", token,
            new { endedAt = _startedAt.AddHours(1) });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SetStatus.Done, SetStatus.Skipped, SetStatus.Skipped], await ReadStatusesAsync(prepared.Id));
    }
}

// The consent rule on POST /workouts/prepared, with the privacy feature on.
public class PreparedWorkoutConsentTests(PrivacyEnabledGymNotebookFactory factory) : IClassFixture<PrivacyEnabledGymNotebookFactory>
{
    [Fact]
    public async Task CreatePrepared_TitleWithoutConsent_Returns403()
    {
        // Arrange
        var (client, userId) = await OptionalDetailsTestData.CreateAccountAsync(factory);

        // Act
        var response = await client.PostAsJsonAsync("/workouts/prepared", new CreatePreparedWorkoutRequest("Push A", null, null, null));

        // Assert: refused as a whole, so not even an empty plan was stored.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("optional_details_consent_required", error!.Code);
        Assert.Empty(await OptionalDetailsTestData.ReadWorkoutsAsync(factory, userId));
    }
}
