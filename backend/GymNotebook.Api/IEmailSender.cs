namespace GymNotebook.Api;

// The one thing the rest of the app knows about delivering email. Three implementations,
// picked once at startup by Email:Backend (EmailOptions, specs/002 plan D7):
//
//   console  ConsoleEmailSender — writes the message to the log. Never in Production.
//   memory   MemoryEmailSender  — keeps messages in a list for the tests to read.
//   resend   ResendEmailSender  — one HTTPS POST to Resend's API. Production.
//
// Nothing calls a sender directly: messages go through EmailOutbox, whose background
// worker applies the caps (EmailCaps) and then calls SendAsync after the HTTP response
// has already been sent. A sender may throw; the worker logs the failure.
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
