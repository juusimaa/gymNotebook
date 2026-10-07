using System.Threading.Channels;

namespace GymNotebook.Api;

// The hand-off between a request and the email it causes (specs/002 plan D6). A handler
// calls Enqueue and returns its response straight away; EmailOutboxWorker sends in the
// background. Two reasons not to send inline: a slow provider must never slow a request,
// and — more important — how long a request takes must not reveal which branch it took
// (an account exists, or doesn't), which an inline send would (spec FR-014).
//
// A Channel is .NET's built-in async producer/consumer queue: writers never block on the
// reader, and the reader awaits new items without polling. Singleton, so every request
// writes into the one queue the worker reads.
public sealed class EmailOutbox(ILogger<EmailOutbox> logger)
{
    // Bounded so a flood of requests can't grow memory without limit. Above the daily cap
    // on purpose: in normal use the queue is empty within a second, so being full means
    // the provider is stuck or something is being abused, and dropping is then the right
    // answer. Wait mode makes TryWrite return false when full, rather than silently
    // dropping (DropWrite) or evicting an older message (DropOldest).
    private const int Capacity = 100;

    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    // Never throws and never waits, so a request can't fail or stall because of email.
    // The message is lost if the queue is full, or later if the container stops before
    // the worker gets to it; the user can ask for the email again (plan D6 accepts this).
    public void Enqueue(EmailMessage message)
    {
        if (!_channel.Writer.TryWrite(message))
        {
            logger.LogWarning("Email queue is full; {Kind} email dropped.", message.Kind);
        }
    }

    // The worker's end of the queue. Internal: nothing outside this assembly should be
    // able to take messages out of it.
    internal ChannelReader<EmailMessage> Reader => _channel.Reader;
}
