using System.Security.Cryptography;
using System.Text;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// specs/002 FR-015, FR-016, SC-003 (tasks.md T013, T015): the per-address and daily caps,
// pruning after 24 hours, and a send log that holds no address. Against real Postgres,
// since the claim is a transaction and ExecuteDelete is SQL a fake provider wouldn't run.
//
// EmailCaps is built directly, with a fixed clock and a chosen daily cap, rather than
// resolved from the host: that's what makes the 24-hour window and the cap testable
// without waiting a day or sending 90 emails. Every test starts from an empty table.
public class EmailCapsTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryClaimAsync_SixthEmailToOneAddress_ReturnsFalse()
    {
        // Arrange
        await ClearAsync();
        var results = new List<bool>();

        // Act
        for (var i = 0; i < EmailCaps.PerAddressPerDay + 1; i++)
        {
            results.Add(await ClaimAsync("ann@example.com"));
        }

        // Assert: five through, the sixth refused and not recorded.
        Assert.Equal([true, true, true, true, true, false], results);
        Assert.Equal(EmailCaps.PerAddressPerDay, await CountAsync());
    }

    [Fact]
    public async Task TryClaimAsync_SameAddressDifferentCaseAndSpacing_SharesOneCap()
    {
        // Arrange
        await ClearAsync();
        for (var i = 0; i < EmailCaps.PerAddressPerDay; i++)
        {
            await ClaimAsync("ann@example.com");
        }

        // Act
        var claimed = await ClaimAsync("  Ann@Example.COM ");

        // Assert
        Assert.False(claimed);
    }

    [Fact]
    public async Task TryClaimAsync_PerAddressCapReached_OtherAddressStillSends()
    {
        // Arrange
        await ClearAsync();
        for (var i = 0; i < EmailCaps.PerAddressPerDay; i++)
        {
            await ClaimAsync("ann@example.com");
        }

        // Act
        var claimed = await ClaimAsync("bob@example.com");

        // Assert
        Assert.True(claimed);
    }

    [Fact]
    public async Task TryClaimAsync_DailyCapReached_ReturnsFalseForNewAddress()
    {
        // Arrange: a cap of three, used up by three different addresses.
        await ClearAsync();
        await ClaimAsync("a@example.com", dailyCap: 3);
        await ClaimAsync("b@example.com", dailyCap: 3);
        await ClaimAsync("c@example.com", dailyCap: 3);

        // Act
        var claimed = await ClaimAsync("d@example.com", dailyCap: 3);

        // Assert
        Assert.False(claimed);
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task TryClaimAsync_RowsOlderThan24Hours_ArePrunedAndNotCounted()
    {
        // Arrange: a full day's cap for this address, all just over 24 hours old, and one
        // row just inside the window.
        await ClearAsync();
        var hash = EmailCaps.HashRecipient("ann@example.com", GymNotebookFactory.JwtSecret);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < EmailCaps.PerAddressPerDay; i++)
            {
                db.EmailSends.Add(new EmailSend { RecipientHash = hash, SentAt = _now.AddHours(-24).AddMinutes(-1) });
            }
            db.EmailSends.Add(new EmailSend { RecipientHash = hash, SentAt = _now.AddHours(-23) });
            await db.SaveChangesAsync();
        }

        // Act
        var claimed = await ClaimAsync("ann@example.com");

        // Assert: the old rows are gone (FR-016), so the address has sent two, not seven.
        Assert.True(claimed);
        await using var check = factory.Services.CreateAsyncScope();
        var sentAt = await check.ServiceProvider.GetRequiredService<AppDbContext>().EmailSends
            .AsNoTracking().Select(s => s.SentAt).ToListAsync();
        Assert.Equal(2, sentAt.Count);
        Assert.All(sentAt, t => Assert.True(t >= _now.AddHours(-24)));
    }

    [Fact]
    public async Task TryClaimAsync_Claim_StoresOnlyKeyedHashOfAddress()
    {
        // Arrange
        await ClearAsync();
        const string address = "ann@example.com";

        // Act
        await ClaimAsync(address);

        // Assert: the stored value is the HMAC keyed with the signing secret — not the
        // address, and not a plain hash anyone could recompute from a guessed address.
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailSends
            .AsNoTracking().Select(s => s.RecipientHash).SingleAsync();
        var expected = Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(GymNotebookFactory.JwtSecret), Encoding.UTF8.GetBytes(address)));
        var unkeyed = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(address)));
        Assert.Equal(expected, stored);
        Assert.NotEqual(unkeyed, stored);
        Assert.DoesNotContain("example.com", stored);
    }

    private async Task<bool> ClaimAsync(string address, int dailyCap = EmailOptions.DefaultDailyCap)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var caps = new EmailCaps(db, GymNotebookFactory.JwtSecret, dailyCap, new FixedTimeProvider(_now));
        return await caps.TryClaimAsync(address, CancellationToken.None);
    }

    private async Task ClearAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailSends.ExecuteDeleteAsync();
    }

    private async Task<int> CountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailSends.CountAsync();
    }
}
