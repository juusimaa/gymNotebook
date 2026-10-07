using System.Net;

namespace GymNotebook.Api;

// Sends what EmailOutbox queues, one message at a time, for as long as the app runs
// (specs/002 plan D6). A BackgroundService is a hosted service the host starts after the
// app is built and stops (cancelling stoppingToken) on shutdown. `--migrate` mode returns
// before the host runs, so this never starts there.
public sealed class EmailOutboxWorker(
    EmailOutbox outbox,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Completes only when the app shuts down: ReadAllAsync waits for the next message
        // and throws OperationCanceledException once stoppingToken fires, which the host
        // expects from a stopping BackgroundService.
        await foreach (var message in outbox.Reader.ReadAllAsync(stoppingToken))
        {
            await DeliverAsync(message, stoppingToken);
        }
    }

    private async Task DeliverAsync(EmailMessage message, CancellationToken stoppingToken)
    {
        try
        {
            // A singleton can't hold scoped services (AppDbContext, and with it EmailCaps),
            // so each message gets its own scope — the background equivalent of one HTTP
            // request's lifetime. The sender is resolved here too: the Resend one is a
            // typed HttpClient, which is meant to be short-lived.
            await using var scope = scopeFactory.CreateAsyncScope();
            var caps = scope.ServiceProvider.GetRequiredService<EmailCaps>();

            if (!await caps.TryClaimAsync(message.To, stoppingToken))
            {
                // A capped send is silent to the caller (FR-015) but not to the operator.
                // Kind only: the address is exactly what must not be logged.
                logger.LogWarning("Email not sent: {Kind} email is over a sending cap.", message.Kind);
                return;
            }

            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            await sender.SendAsync(message, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: let ExecuteAsync end. The message is lost, as plan D6 accepts.
            throw;
        }
        catch (Exception ex)
        {
            // The one boundary where a broad catch is right: there's no request left to
            // fail, and an exception escaping ExecuteAsync would stop the worker — every
            // later email would then queue up and never leave. Logged as a short
            // description instead of the exception itself, because an exception's message
            // isn't ours to vouch for and could quote the recipient.
            logger.LogError("Email send failed: {Kind} email, {Failure}.", message.Kind, Describe(ex));
        }
    }

    // Enough to tell a bad key or unverified domain (HTTP 4xx) from Resend being down
    // (5xx) or slow (timeout) or the database failing (the exception type).
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode status } => $"HTTP {(int)status}",
        // HttpClient.Timeout surfaces as a TaskCanceledException whose own token never fired.
        TaskCanceledException => "timed out",
        _ => ex.GetType().Name,
    };
}
