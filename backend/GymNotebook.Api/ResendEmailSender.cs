using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace GymNotebook.Api;

// Email:Backend = resend. One POST to https://api.resend.com/emails per message — the
// whole of Resend's API this app needs, so no SDK package (specs/002 plan, Primary
// Dependencies). A "typed client": Program.cs registers it with AddHttpClient, which hands
// it an HttpClient with the base address and the 10-second timeout already set, and pools
// the underlying connections so creating a sender per message costs nothing.
public sealed class ResendEmailSender(HttpClient http, EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        // Resend's request body. Both parts are sent: mail clients that can't or won't
        // render HTML show the text one. No tracking options are set here; open and click
        // tracking are off on the sending domain itself (plan, Go-live step 1).
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new ResendEmailRequest(
                options.From, [message.To], message.Subject, message.Text, message.Html)),
        };

        // Set per request rather than on the shared client's default headers, so the key
        // lives in exactly one place: the options loaded at startup.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResendApiKey);

        using var response = await http.SendAsync(request, cancellationToken);

        // A non-2xx answer (bad key, unverified domain, Resend's own rate limit) becomes an
        // HttpRequestException carrying only the status code, which the outbox worker logs.
        // Resend's error body is not read: it can echo the recipient back.
        response.EnsureSuccessStatusCode();
    }

    // The JSON body. Property names serialize camelCase (JsonContent's web defaults), which
    // for these single-word names is exactly what Resend expects: from, to, subject, ...
    private sealed record ResendEmailRequest(string From, string[] To, string Subject, string Text, string Html);
}
