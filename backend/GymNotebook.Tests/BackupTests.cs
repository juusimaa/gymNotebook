using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GymNotebook.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace GymNotebook.Tests;

// Spec 004 PR 2: the timestamp describes a completed full backup, never a CSV
// conversion, rejected request or incomplete stream. All persistence is real Postgres.
[Collection("Lifecycle")]
public class BackupTests(TwoHostGymNotebookFixture db)
{
    [Theory]
    [InlineData("json")]
    [InlineData(null)]
    public async Task Export_JsonCompletes_StampsLastBackupAt(string? format)
    {
        // Arrange: null here omits the property, exercising the existing client's body.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        var otherId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        var before = DateTimeOffset.UtcNow;

        // Act
        using var response = format is null
            ? await ExportTestSupport.ExportAsync(client)
            : await client.PostAsJsonAsync("/account/export", new { currentPassword = TwoHostGymNotebookFixture.Password, format });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await WaitForExportEndAsync(userId);

        // Assert: completion stamps only this account; the file predates that stamp.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stamp = await ReadStampAsync(userId);
        Assert.NotNull(stamp);
        Assert.InRange(stamp.Value, before, DateTimeOffset.UtcNow);
        Assert.Null(await ReadStampAsync(otherId));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("account").GetProperty("lastBackupAt").ValueKind);
        using var status = await client.GetAsync("/account/backup");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True(status.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal(stamp.Value, body.RootElement.GetProperty("lastBackupAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task GetBackup_NoBackup_ReturnsNull(string flag)
    {
        // Arrange
        await using var host = db.CreateHost(new Dictionary<string, string?> { ["PRIVACY_LIFECYCLE_ENABLED"] = flag });
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        using var response = await client.GetAsync("/account/backup");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("lastBackupAt").ValueKind);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("")]
    [InlineData("JSON")]
    [InlineData(" json ")]
    [InlineData(null)]
    public async Task Export_UnknownFormat_Returns400(string? format)
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act: explicit null is an invalid value, unlike an omitted field.
        using var response = await client.PostAsJsonAsync("/account/export", new { currentPassword = TwoHostGymNotebookFixture.Password, format });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(await ReadStampAsync(userId));
    }

    [Fact]
    public async Task Export_Csv_DoesNotStamp()
    {
        // Arrange: an existing backup must survive a spreadsheet download.
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        await SeedPreviousStampAsync(userId);

        // Act
        using var response = await client.PostAsJsonAsync("/account/export", new { currentPassword = TwoHostGymNotebookFixture.Password, format = "csv" });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await WaitForExportEndAsync(userId);

        // Assert: CSV still receives the complete JSON snapshot for browser conversion.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(_previousStamp, await ReadStampAsync(userId));
    }

    [Fact]
    public async Task Export_WrongPassword_DoesNotStamp()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        await SeedPreviousStampAsync(userId);

        // Act
        using var response = await ExportTestSupport.ExportAsync(client, "wrong-password");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(_previousStamp, await ReadStampAsync(userId));
    }

    [Fact]
    public async Task Export_IncludesPreviousLastBackupAt()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        await SeedPreviousStampAsync(userId);

        // Act
        using var response = await ExportTestSupport.ExportAsync(client);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await WaitForExportEndAsync(userId);

        // Assert
        Assert.Equal(_previousStamp, document.RootElement.GetProperty("account").GetProperty("lastBackupAt").GetDateTimeOffset());
        Assert.True(await ReadStampAsync(userId) > _previousStamp);
    }

    [Fact]
    public async Task DeleteAccount_RemovesLastBackupAt()
    {
        // Arrange
        await using var host = db.CreateHost(ExportTestSupport.FlagOn());
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        await SeedPreviousStampAsync(userId);
        Assert.Equal(_previousStamp, await ReadStampAsync(userId));

        // Act
        using var response = await DeletionTestSupport.DeleteAsync(client);

        // Assert: the timestamp lives only on the deleted user row.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await DeletionTestSupport.SnapshotAccountAsync(db, userId));
        using var status = await client.GetAsync("/account/backup");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Export_StampWriteFails_LeavesCompleteFileAndPreviousTime()
    {
        // Arrange: a real row lock makes only the post-delivery UPDATE time out.
        // The read-only snapshot and all advisory delivery checks can still finish.
        var logs = new CapturingLoggerProvider();
        await using var host = db.CreateHost(ExportTestSupport.FlagOn(("Lifecycle:SharedLockTimeoutMs", "100")),
            services: s => s.AddLogging(b => b.AddProvider(logs)));
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);
        await SeedPreviousStampAsync(userId);
        await using var locked = await DeletionTestSupport.HoldUserRowLockAsync(db, userId);

        // Act
        using var response = await ExportTestSupport.ExportAsync(client);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await WaitForExportEndAsync(userId);

        // Assert: failure is only an anonymous warning, not a broken download.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_previousStamp, document.RootElement.GetProperty("account").GetProperty("lastBackupAt").GetDateTimeOffset());
        Assert.Equal(_previousStamp, await ReadStampAsync(userId));
        var warning = Assert.Single(logs.Entries, e => e.Category == typeof(NotebookExport).FullName && e.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        Assert.Single(warning.State); // only the constant message template, no account data
    }

    [Theory]
    [InlineData("/account/backup")]
    [InlineData("/account/export")]
    public async Task Backup_Unauthenticated_Returns401WithNoStore(string path)
    {
        // Arrange
        await using var host = db.CreateHost();
        using var client = host.CreateClient();

        // Act
        using var response = path.EndsWith("export", StringComparison.Ordinal)
            ? await client.PostAsJsonAsync(path, new { currentPassword = "irrelevant" })
            : await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Export_MalformedBody_Returns400WithNoStore()
    {
        // Arrange
        await using var host = db.CreateHost();
        var userId = await db.SeedUserAsync();
        using var client = host.ClientFor(userId);

        // Act
        using var response = await client.PostAsync("/account/export", new StringContent("{", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("delete")]
    [InlineData("expire")]
    [InlineData("disconnect")]
    public async Task Export_AuthorizationEndsAfterFinalFlush_DoesNotStamp(string change)
    {
        // Arrange: exercise the real exporter and Postgres, controlling only the response
        // stream to reach the tiny window between the final flush and the stamp guard.
        var userId = await db.SeedUserAsync();
        await SeedPreviousStampAsync(userId);
        await using var context = db.NewContext();
        var clock = new OffsetTimeProvider();
        using var disconnected = new CancellationTokenSource();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", userId.ToString(CultureInfo.InvariantCulture)),
                new Claim("tv", "0"),
                new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ], "test")),
            RequestAborted = disconnected.Token,
        };
        var flushed = false;
        await using var body = new FinalFlushStream(async () =>
        {
            // Empty account: the entire document goes in one chunk. Nothing has been
            // stamped before that chunk finishes flushing.
            Assert.Equal(_previousStamp, await ReadStampAsync(userId));
            flushed = true;
            if (change == "expire") clock.Offset = TimeSpan.FromHours(1);
            else if (change == "disconnect") disconnected.Cancel();
            else
            {
                await using var exclusive = await db.HoldExclusiveAsync(userId);
                if (change == "delete") await exclusive.DeleteUserAndCommitAsync();
                else await exclusive.BumpTokenVersionAndCommitAsync();
            }
        });
        http.Response.Body = body;
        var export = new NotebookExport(context, new LifecycleOptions(), clock, NullLogger<NotebookExport>.Instance);

        // Act
        await export.RunAsync(new ExportRequest(TwoHostGymNotebookFixture.Password), http);

        // Assert: the already-complete file survives, but no invalidated caller stamps.
        Assert.True(flushed);
        using var document = JsonDocument.Parse(body.ToArray());
        Assert.Equal(_previousStamp, document.RootElement.GetProperty("account").GetProperty("lastBackupAt").GetDateTimeOffset());
        Assert.Equal(change == "delete" ? null : _previousStamp, await ReadStampAsync(userId));
        Assert.Equal(0, await db.CountExportLocksAsync(userId));
    }

    private sealed class FinalFlushStream(Func<Task> onFlush) : MemoryStream
    {
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            await onFlush();
        }
    }

    private static readonly DateTimeOffset _previousStamp = new(2026, 10, 3, 6, 42, 11, TimeSpan.Zero);

    private Task SeedPreviousStampAsync(int userId) => db.ExecuteAsync(
        "UPDATE users SET last_backup_at = TIMESTAMPTZ '2026-10-03 06:42:11Z' WHERE id = @id", userId);

    private async Task<DateTimeOffset?> ReadStampAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT last_backup_at FROM users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        var result = await command.ExecuteScalarAsync();
        return result is DateTime value ? new DateTimeOffset(value) : null;
    }

    private Task WaitForExportEndAsync(int userId) => TwoHostGymNotebookFixture.WaitForAsync(
        async () => await db.CountExportLocksAsync(userId) == 0,
        TimeSpan.FromSeconds(10), "export and its timestamp write to finish");
}
