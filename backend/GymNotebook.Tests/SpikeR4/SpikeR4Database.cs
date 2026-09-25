using System.Diagnostics;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GymNotebook.Tests.SpikeR4;

// SPIKE (T008, research R4 → Validation spike Part A). Throwaway; never merged. Candidate
// starting point for T010's two-host fixture.
//
// One Postgres container shared by every spike test class (an xUnit collection fixture),
// from which each test builds as many app hosts as it needs. Two hosts pointed at one
// database are two "app instances" as far as the advisory locks are concerned: nothing
// in process is shared between them except the SpikeHooks barrier object the test passes
// to both.
public sealed class SpikeR4Database : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    public AppDbContext NewContext(string? connectionString = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString ?? ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public SpikeHost CreateHost(SpikeHooks hooks, IDictionary<string, string?>? settings = null, bool kestrel = false,
        Action<KestrelServerOptions>? kestrelOptions = null, string? connectionString = null) =>
        new(connectionString ?? ConnectionString, hooks, settings ?? new Dictionary<string, string?>(), kestrel, kestrelOptions);

    public const string Password = "spike-password";

    // Seeded directly (not via /auth/register) to stay clear of the rate limiter; BCrypt
    // cost 4 keeps seeding fast; Verify reads the cost from the hash itself.
    public async Task<int> SeedUserAsync()
    {
        await using var db = NewContext();
        var user = new User
        {
            Username = $"spike-{Guid.NewGuid():N}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public static string Token(int userId, int tokenVersion = 0) =>
        JwtTokenFactory.CreateToken(
            new User { Id = userId, Username = "", PasswordHash = "", TokenVersion = tokenVersion },
            GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);

    // workouts × blocksPerWorkout blocks × setsPerBlock sets, one exercise per block slot.
    public async Task<List<int>> SeedNotebookAsync(int userId, int workouts, int blocksPerWorkout, int setsPerBlock)
    {
        await using var db = NewContext();
        var exercises = Enumerable.Range(0, blocksPerWorkout)
            .Select(i => new Exercise { UserId = userId, Name = $"Exercise {i}", NormalizedName = $"exercise {i}" })
            .ToList();
        db.Exercises.AddRange(exercises);
        await db.SaveChangesAsync();

        var seeded = new List<Workout>();
        for (var w = 0; w < workouts; w++)
        {
            var workout = new Workout
            {
                UserId = userId,
                Date = new DateOnly(2026, 1, 1).AddDays(w),
                StartedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero).AddDays(w),
            };
            for (var b = 0; b < blocksPerWorkout; b++)
            {
                var block = new WorkoutExercise { ExerciseId = exercises[b].Id, Position = b };
                for (var s = 1; s <= setsPerBlock; s++)
                {
                    block.SetEntries.Add(new SetEntry { SetNumber = s, Reps = 5, Weight = 100 });
                }
                workout.WorkoutExercises.Add(block);
            }
            seeded.Add(workout);
        }
        db.Workouts.AddRange(seeded);
        await db.SaveChangesAsync();
        return seeded.Select(w => w.Id).ToList();
    }

    // One workout, one block per exercise, `setsPerBlock` sets each — generated in SQL so a
    // multi-megabyte response (bigger than every socket buffer on the path) seeds quickly.
    public async Task<int> SeedHugeWorkoutAsync(int userId, int blocks, int setsPerBlock)
    {
        var workoutId = (await SeedNotebookAsync(userId, 1, blocks, 1)).Single();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO set_entries (workout_exercise_id, set_number, weight, reps, is_warmup)
            SELECT we.id, n, 100, 5, false
            FROM workout_exercises we, generate_series(2, @sets) AS n
            WHERE we.workout_id = @workout
            """, connection);
        command.Parameters.AddWithValue("sets", setsPerBlock);
        command.Parameters.AddWithValue("workout", workoutId);
        await command.ExecuteNonQueryAsync();
        return workoutId;
    }

    public async Task<bool> UserExistsAsync(int userId)
    {
        await using var db = NewContext();
        return await db.Users.AnyAsync(u => u.Id == userId);
    }

    // Open "idle in transaction" sessions other than our own: a transaction someone forgot
    // to end (e.g. an export snapshot not disposed after an abort).
    public async Task<int> CountIdleInTransactionAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND state LIKE 'idle in transaction%' AND pid <> pg_backend_pid()",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // Blocks until `count` sessions are *waiting* (not granted) on the lifecycle lock for
    // userId. This is the explicit synchronization the tests use instead of sleeps: it
    // proves a request has reached its lock wait before the test acts. Two-int advisory
    // locks show up in pg_locks with classid = first key, objid = second key, objsubid = 2.
    public async Task WaitForLockWaitersAsync(int userId, int count, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid = @ns::oid AND objid = @id::oid AND objsubid = 2",
                connection);
            command.Parameters.AddWithValue("ns", (long)LifecycleLocks.Namespace);
            command.Parameters.AddWithValue("id", (long)userId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) >= count)
            {
                return;
            }
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Fewer than {count} waiter(s) on the lifecycle lock for user {userId} after {timeout}.");
            }
            await Task.Delay(10);
        }
    }

    // Takes exclusive access for userId on a connection the test controls, standing in for
    // a deletion that the test can hold open and then commit at a moment of its choosing.
    public async Task<ExclusiveHolder> HoldExclusiveAsync(int userId)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, @id)", connection, transaction))
        {
            command.Parameters.AddWithValue("ns", LifecycleLocks.Namespace);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }
        return new ExclusiveHolder(connection, transaction, userId);
    }

    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for: {what}");
            }
            await Task.Delay(10);
        }
    }
}

public sealed class ExclusiveHolder(NpgsqlConnection connection, NpgsqlTransaction transaction, int userId) : IAsyncDisposable
{
    public async Task DeleteUserAndCommitAsync()
    {
        foreach (var sql in new[]
        {
            "DELETE FROM workouts WHERE user_id = @id",
            "DELETE FROM exercises WHERE user_id = @id",
            "DELETE FROM users WHERE id = @id",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}

public sealed class SpikeHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly SpikeHooks _hooks;
    private readonly IDictionary<string, string?> _settings;

    public SpikeHost(string connectionString, SpikeHooks hooks, IDictionary<string, string?> settings, bool kestrel, Action<KestrelServerOptions>? kestrelOptions)
    {
        _connectionString = connectionString;
        _hooks = hooks;
        _settings = settings;
        if (kestrel)
        {
            if (kestrelOptions is null)
            {
                UseKestrel(0);
            }
            else
            {
                UseKestrel(kestrelOptions);
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:Secret", GymNotebookFactory.JwtSecret);
        builder.UseSetting("Jwt:ExpiryMinutes", GymNotebookFactory.JwtExpiryMinutes.ToString());
        builder.UseSetting("INVITE_CODE", "");
        builder.UseSetting("CORS_ORIGINS", GymNotebookFactory.AllowedOrigin);
        builder.UseSetting("Spike:R4:Enabled", "true");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Warning");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services => services.AddSingleton(_hooks));
    }

    public HttpClient ClientFor(int userId, int tokenVersion = 0)
    {
        var client = CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.Authorization = new("Bearer", SpikeR4Database.Token(userId, tokenVersion));
        return client;
    }
}

[CollectionDefinition("SpikeR4")]
public class SpikeR4Collection : ICollectionFixture<SpikeR4Database>;
