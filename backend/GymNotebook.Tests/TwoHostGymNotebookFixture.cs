using System.Data.Common;
using System.Diagnostics;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GymNotebook.Tests;

// The fixture behind the account-lifecycle concurrency tests (specs/001 research R4).
// One PostgreSQL container shared by every test class in the "Lifecycle" collection,
// from which each test builds as many app hosts as it needs. Two hosts pointed at one
// database are two app instances as far as the advisory locks are concerned — nothing in
// process is shared between them — which is the multi-replica case the locks exist for.
//
// Tests synchronize through the database, not sleeps: a request is known to be waiting
// on the lifecycle lock when pg_locks shows it (WaitForLockWaitersAsync), and a test
// stands in for a deletion or password change by holding exclusive access on a
// connection it controls (HoldExclusiveAsync). For the one interleaving the database
// can't expose — "a write has committed but not yet delivered" — CommitBarrier pauses a
// host right after an EF commit. None of this adds test hooks to the API itself.
public sealed class TwoHostGymNotebookFixture : IAsyncLifetime
{
    public const string Password = "lifecycle-test-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // A context configured exactly like the app's (Npgsql + snake_case), for seeding and
    // for asserting on what the database really holds after a request.
    public AppDbContext NewContext(string? connectionString = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString ?? ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public LifecycleTestHost CreateHost(
        IDictionary<string, string?>? settings = null,
        Action<KestrelServerOptions>? kestrel = null,
        Action<IServiceCollection>? services = null) =>
        new(ConnectionString, settings ?? new Dictionary<string, string?>(), kestrel, services);

    // An empty, unmigrated database on the same server, for tests that need to control
    // which migrations have run (the backfill test in AccountIdentityTests).
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"db_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        // CREATE DATABASE can't take a bind parameter; the name is generated above.
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    // Seeded directly rather than through /auth/register, to stay clear of the in-process
    // rate limiter. BCrypt cost 4 keeps seeding fast; Verify reads the cost from the hash.
    public async Task<int> SeedUserAsync()
    {
        await using var db = NewContext();
        var user = new User
        {
            Username = $"lifecycle-{Guid.NewGuid():N}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4),
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public static string Token(int userId, int tokenVersion = 0) =>
        JwtTokenFactory.CreateToken(
            new User { Id = userId, Username = "", PasswordHash = "", TokenVersion = tokenVersion, PrivacyAccountId = Guid.Empty },
            GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);

    // `workouts` workouts × `blocksPerWorkout` blocks × `setsPerBlock` sets, one exercise
    // per block slot. Returns the workout ids.
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

    // One workout whose JSON is several megabytes — far beyond every socket buffer on the
    // path — so a guarded read's response write genuinely blocks when the client stops
    // reading. The sets are generated in SQL so seeding stays fast.
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

    public async Task ExecuteAsync(string sql, int userId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync();
    }

    // Counts sessions on the lifecycle lock for userId, either waiting (not granted) or
    // holding it. Two-int advisory locks show up in pg_locks with classid = first key,
    // objid = second key and objsubid = 2.
    public async Task<int> CountLifecycleLocksAsync(int userId, bool granted)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted = @granted AND classid = @ns::oid AND objid = @id::oid AND objsubid = 2",
            connection);
        command.Parameters.AddWithValue("granted", granted);
        command.Parameters.AddWithValue("ns", (long)AccountLifecycle.LockNamespace);
        command.Parameters.AddWithValue("id", (long)userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // Blocks until at least `count` sessions are waiting on the lifecycle lock for userId:
    // the proof that a request has reached its lock wait before the test acts.
    public async Task WaitForLockWaitersAsync(int userId, int count, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        while (await CountLifecycleLocksAsync(userId, granted: false) < count)
        {
            if (stopwatch.Elapsed > limit)
            {
                throw new TimeoutException($"Fewer than {count} waiter(s) on the lifecycle lock for user {userId} after {limit}.");
            }
            await Task.Delay(10);
        }
    }

    // Takes exclusive lifecycle access for userId on a connection the test controls,
    // standing in for a deletion or password change the test can hold open and then
    // commit at the moment of its choosing.
    public async Task<ExclusiveHolder> HoldExclusiveAsync(int userId)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, @id)", connection, transaction))
        {
            command.Parameters.AddWithValue("ns", AccountLifecycle.LockNamespace);
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }
        return new ExclusiveHolder(connection, transaction, userId);
    }

    public static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for: {what}");
            }
            await Task.Delay(10);
        }
    }
}

// An open transaction holding exclusive lifecycle access, standing in for the deletion
// (research R5 delete order) or password change that a test commits on cue.
public sealed class ExclusiveHolder(NpgsqlConnection connection, NpgsqlTransaction transaction, int userId) : IAsyncDisposable
{
    public async Task DeleteUserAndCommitAsync()
    {
        // Workouts cascade to blocks and sets; exercises only after, because blocks
        // reference exercises with ON DELETE RESTRICT.
        await ExecuteAsync("DELETE FROM workouts WHERE user_id = @id");
        await ExecuteAsync("DELETE FROM exercises WHERE user_id = @id");
        await ExecuteAsync("DELETE FROM users WHERE id = @id");
        await transaction.CommitAsync();
    }

    // What a password change does to every token issued so far.
    public async Task BumpTokenVersionAndCommitAsync()
    {
        await ExecuteAsync("UPDATE users SET token_version = token_version + 1 WHERE id = @id");
        await transaction.CommitAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}

// Pauses the host's next EF transaction commit *after* it has committed, until the test
// releases it: exactly the window between a write's commit and its delivery guard. Armed
// once; later commits pass straight through. Registered only in the test host.
public sealed class CommitBarrier : DbTransactionInterceptor
{
    private int _armed = 1;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    public void Release() => _release.TrySetResult();

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reached.TrySetResult();
            await _release.Task;
        }
    }
}

// One app instance on the shared database. Real Kestrel on a loopback port when `kestrel`
// is given (streaming tests need real socket buffers; TestServer's in-memory transport has
// none), otherwise the usual in-memory TestServer.
public sealed class LifecycleTestHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IDictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _services;

    public LifecycleTestHost(string connectionString, IDictionary<string, string?> settings,
        Action<KestrelServerOptions>? kestrel, Action<IServiceCollection>? services)
    {
        _connectionString = connectionString;
        _settings = settings;
        _services = services;
        if (kestrel is not null)
        {
            UseKestrel(kestrel);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The same pinned configuration as GymNotebookFactory, so these hosts boot the app
        // exactly as the rest of the suite does.
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:Secret", GymNotebookFactory.JwtSecret);
        builder.UseSetting("Jwt:ExpiryMinutes", GymNotebookFactory.JwtExpiryMinutes.ToString());
        builder.UseSetting("INVITE_CODE", "");
        builder.UseSetting("PRIVACY_LIFECYCLE_ENABLED", "false");
        builder.UseSetting("CORS_ORIGINS", GymNotebookFactory.AllowedOrigin);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        if (_services is not null)
        {
            builder.ConfigureTestServices(_services);
        }
    }

    public HttpClient ClientFor(int userId, int tokenVersion = 0)
    {
        var client = CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TwoHostGymNotebookFixture.Token(userId, tokenVersion));
        return client;
    }
}

[CollectionDefinition("Lifecycle")]
public class LifecycleCollection : ICollectionFixture<TwoHostGymNotebookFixture>;
