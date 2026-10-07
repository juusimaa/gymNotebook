using System.Collections.Concurrent;

namespace GymNotebook.Api;

// Email:Backend = memory. Keeps every message it is given, oldest first, so a test can
// read the link out of an email exactly as a user would (specs/002 plan, Testing).
// Registered as a singleton (Program.cs) so the test host's one instance collects the
// messages from every request. A concurrent queue because the outbox worker adds while a
// test thread reads.
public sealed class MemoryEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public IReadOnlyList<EmailMessage> Messages => _messages.ToList();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}
