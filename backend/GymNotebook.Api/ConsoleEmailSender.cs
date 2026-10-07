namespace GymNotebook.Api;

// Email:Backend = console. "Sends" by writing the plain-text body to the log, so a local
// run needs no email account: the confirmation link is in `docker compose logs backend`
// (specs/002 quickstart.md). The body holds a live link, which is exactly why EmailOptions
// refuses this backend in Production — in production these lines would hand anyone
// who can read the logs a way into someone's account.
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Email ({Kind}) to {To}: {Subject}\n{Text}", message.Kind, message.To, message.Subject, message.Text);
        return Task.CompletedTask;
    }
}
