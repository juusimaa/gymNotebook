using System.Net;
using System.Text.Json;
using GymNotebook.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GymNotebook.Tests;

// specs/002 plan D6 (tasks.md T012, T015): what a handler enqueues reaches the sender in
// the background, the caps apply on that path, and failures are logged by kind — never
// with the address — without stopping the worker.
public class EmailOutboxTests(EmailOutboxGymNotebookFactory factory) : IClassFixture<EmailOutboxGymNotebookFactory>
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Enqueue_Message_IsDeliveredBySender()
    {
        // Arrange
        var to = UniqueAddress();
        var message = Message("confirmation", to);

        // Act
        factory.Services.GetRequiredService<EmailOutbox>().Enqueue(message);

        // Assert
        await WaitForAsync(() => factory.Sender.Delivered.Any(m => m.To == to), "the message to be delivered");
        Assert.Equal(message, factory.Sender.Delivered.Single(m => m.To == to));
    }

    [Fact]
    public async Task Enqueue_OverPerAddressCap_DropsExtraAndLogsWarningWithoutAddress()
    {
        // Arrange
        var to = UniqueAddress();
        var outbox = factory.Services.GetRequiredService<EmailOutbox>();

        // Act
        for (var i = 0; i < EmailCaps.PerAddressPerDay + 1; i++)
        {
            outbox.Enqueue(Message("confirmation", to));
        }

        // Assert: five delivered, the sixth refused with a warning naming only the kind.
        await WaitForAsync(() => factory.Logs.Entries.Any(e => e.Message.Contains("over a sending cap")), "the cap warning");
        Assert.Equal(EmailCaps.PerAddressPerDay, factory.Sender.Delivered.Count(m => m.To == to));
        var warning = factory.Logs.Entries.Single(e => e.Message.Contains("over a sending cap"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("confirmation", warning.Message);
        AssertNoLogMentions(to);
    }

    [Fact]
    public async Task Enqueue_SenderFails_LogsStatusWithoutAddressAndKeepsSending()
    {
        // Arrange: the fake sender throws for this kind, the way ResendEmailSender does
        // when Resend answers 422.
        var failing = UniqueAddress();
        var next = UniqueAddress();
        var outbox = factory.Services.GetRequiredService<EmailOutbox>();

        // Act
        outbox.Enqueue(Message(FlakyEmailSender.FailingKind, failing));
        outbox.Enqueue(Message("password_reset", next));

        // Assert: the failure is logged with its status, and the worker is still running.
        await WaitForAsync(() => factory.Sender.Delivered.Any(m => m.To == next), "the message after the failure");
        var error = factory.Logs.Entries.Single(e => e.Level == LogLevel.Error && e.Message.Contains(FlakyEmailSender.FailingKind));
        Assert.Contains("HTTP 422", error.Message);
        Assert.Null(error.Exception);
        AssertNoLogMentions(failing);
    }

    private void AssertNoLogMentions(string address) =>
        Assert.DoesNotContain(factory.Logs.Entries, e =>
            e.Message.Contains(address, StringComparison.OrdinalIgnoreCase)
            || e.State.Any(p => p.Value?.ToString()?.Contains(address, StringComparison.OrdinalIgnoreCase) == true));

    private static EmailMessage Message(string kind, string to) =>
        new(kind, to, "Subject", "Body https://app.example.test/verify-email#token=t", "<p>Body</p>");

    // Unique per test, so the shared host's caps never carry over from another test.
    private static string UniqueAddress() => $"{Guid.NewGuid():N}@example.com";

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TwoHostGymNotebookFixture.WaitForAsync(() => Task.FromResult(condition()), _timeout, what);
}

// The base fixture with the logs captured and a sender that can be told to fail.
public class EmailOutboxGymNotebookFactory : GymNotebookFactory
{
    public CapturingLoggerProvider Logs { get; } = new();
    public FlakyEmailSender Sender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);
            // Registered after Program.cs's memory sender, so it's the one resolved.
            services.AddSingleton<IEmailSender>(Sender);
        });
    }
}

// Records what it delivers, and throws a 422 HttpRequestException for FailingKind.
public sealed class FlakyEmailSender : IEmailSender
{
    public const string FailingKind = "always_fails";

    private readonly MemoryEmailSender _delivered = new();

    public IReadOnlyList<EmailMessage> Delivered => _delivered.Messages;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        message.Kind == FailingKind
            ? throw new HttpRequestException($"Rejected {message.To}", null, HttpStatusCode.UnprocessableEntity)
            : _delivered.SendAsync(message, cancellationToken);
}

// ResendEmailSender's request, checked against a stub handler instead of the network.
public class ResendEmailSenderTests
{
    [Fact]
    public async Task SendAsync_Message_PostsJsonWithBearerKey()
    {
        // Arrange
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") };
        var options = new EmailOptions
        {
            Backend = EmailOptions.ResendBackend,
            From = "Gym Notebook <no-reply@mail.gymnotebook.fit>",
            AppUrl = "https://gymnotebook.fit",
            ResendApiKey = "re_test",
            DailyCap = EmailOptions.DefaultDailyCap,
        };
        var sender = new ResendEmailSender(http, options);

        // Act
        await sender.SendAsync(new EmailMessage("confirmation", "ann@example.com", "Hi", "text", "<p>html</p>"), CancellationToken.None);

        // Assert
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.resend.com/emails", handler.Uri?.ToString());
        Assert.Equal("Bearer re_test", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Gym Notebook <no-reply@mail.gymnotebook.fit>", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("ann@example.com", body.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("Hi", body.RootElement.GetProperty("subject").GetString());
        Assert.Equal("text", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("<p>html</p>", body.RootElement.GetProperty("html").GetString());
    }

    [Fact]
    public async Task SendAsync_ResendRejects_ThrowsWithStatusCode()
    {
        // Arrange
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.Forbidden)) { BaseAddress = new Uri("https://api.resend.com/") };
        var options = new EmailOptions { Backend = EmailOptions.ResendBackend, From = "x <x@example.com>", AppUrl = "https://example.com", ResendApiKey = "re_test", DailyCap = 1 };
        var sender = new ResendEmailSender(http, options);

        // Act
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sender.SendAsync(new EmailMessage("confirmation", "ann@example.com", "Hi", "text", "html"), CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}
