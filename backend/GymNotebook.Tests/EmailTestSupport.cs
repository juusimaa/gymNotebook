using System.Net;
using System.Net.Http.Json;
using GymNotebook.Api;
using GymNotebook.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// The signup flow as a user goes through it (specs/002 plan, Testing): register, read the
// link out of the email the memory backend captured, confirm, sign in. For tests that are
// about something that needs a real, confirmed account made through the API.
//
// Email leaves through a background worker (EmailOutbox), after the response, so a test
// can't read it the moment register returns — it waits for it, up to a few seconds.
public static class EmailTestSupport
{
    public const string Password = "correct-horse-battery-staple";

    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    // A fresh address per call: every test in a class shares one database.
    public static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.test";

    public static MemoryEmailSender Inbox(IServiceProvider services) => services.GetRequiredService<MemoryEmailSender>();

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password = Password, string displayName = "Test user") =>
        client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password, displayName, null));

    // Register → confirmation email → POST /auth/verify-email → login. Returns the session
    // token, so the test continues exactly where a real user would be after signing up.
    public static async Task<string> RegisterConfirmedAsync(HttpClient client, IServiceProvider services, string email, string password = Password)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(client, email, password)).StatusCode);
        var message = await WaitForMessageAsync(services, email, "confirmation");

        var verify = await client.PostAsJsonAsync("/auth/verify-email", new VerifyEmailRequest(LinkToken(message)));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
    }

    // An account written straight to the database, confirmed or not — for tests about
    // something other than the signup itself, which then don't spend the "auth" rate-limit
    // bucket all tests in a class share. BCrypt cost 4 keeps it fast; Verify reads the cost
    // from the hash, so login works as usual.
    public static async Task<User> SeedUserAsync(IServiceProvider services, string? email = null, bool confirmed = true, string password = Password)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Email = email ?? UniqueEmail(),
            DisplayName = "Test user",
            EmailVerifiedAt = confirmed ? DateTimeOffset.UtcNow : null,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, 4),
            PrivacyAccountId = Guid.NewGuid(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    // A session token for a seeded user, minted the way login does.
    public static string SessionToken(User user) =>
        JwtTokenFactory.CreateToken(user, GymNotebookFactory.JwtSecret, GymNotebookFactory.JwtExpiryMinutes);

    // Every message sent to `to` so far, oldest first.
    public static IReadOnlyList<EmailMessage> MessagesTo(IServiceProvider services, string to) =>
        Inbox(services).Messages.Where(m => m.To == to).ToList();

    // Waits until `to` has received `count` messages in total, then returns the newest one,
    // which must be of `kind`. `count` lets a test wait for a second email to the same
    // address rather than finding the first one again.
    public static async Task<EmailMessage> WaitForMessageAsync(IServiceProvider services, string to, string kind, int count = 1)
    {
        var message = (await WaitForMessagesAsync(services, to, count))[count - 1];
        Assert.Equal(kind, message.Kind);
        return message;
    }

    // Waits until `to` has received at least `count` messages; returns them all, oldest
    // first. For when the order they arrive in isn't fixed.
    public static async Task<IReadOnlyList<EmailMessage>> WaitForMessagesAsync(IServiceProvider services, string to, int count)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (true)
        {
            var messages = MessagesTo(services, to);
            if (messages.Count >= count)
            {
                return messages;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Only {messages.Count} of {count} emails arrived within {_wait.TotalSeconds} s.");
            await Task.Delay(20);
        }
    }

    // For asserting that nothing is sent. The outbox is a FIFO with one reader, so once a
    // probe message queued *after* the action has been delivered, anything the action
    // queued would have been delivered before it.
    public static async Task DrainOutboxAsync(IServiceProvider services)
    {
        var probe = $"probe-{Guid.NewGuid():N}@example.test";
        var templates = services.GetRequiredService<EmailTemplates>();
        services.GetRequiredService<EmailOutbox>().Enqueue(templates.AlreadyRegistered(probe));
        await WaitForMessageAsync(services, probe, "already_registered");
    }

    // The token from a link "…#token=<token>" in the text part — what the frontend page
    // would read from the URL fragment.
    public static string LinkToken(EmailMessage message)
    {
        const string marker = "#token=";
        var start = message.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The {message.Kind} email has no link token.");
        start += marker.Length;
        var end = message.Text.IndexOfAny(['\n', ' '], start);
        return Uri.UnescapeDataString(end < 0 ? message.Text[start..] : message.Text[start..end]);
    }
}
