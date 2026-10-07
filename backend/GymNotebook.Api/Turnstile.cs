using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GymNotebook.Api;

// Cloudflare Turnstile: the bot check on the two routes that email an address nobody has
// proven yet, POST /auth/register and POST /auth/password-reset (specs/002 plan D9,
// FR-019). The per-IP rate limits key on an address a botnet has thousands of; this is
// what stops a script from walking a list of other people's addresses through either
// route and filling their inboxes.
//
// The browser solves the widget and sends the token it got with the form; this class asks
// Cloudflare's siteverify endpoint whether that token is good. One HTTPS call, so a typed
// HttpClient (registered in Program.cs with the base address and a 10-second timeout) and
// no SDK package, like ResendEmailSender.
//
// For local testing Cloudflare publishes keys that always pass:
//   site key   1x00000000000000000000AA
//   secret     1x0000000000000000000000000000000AA
// Their answers carry the hostname example.com, so local testing sets
// TURNSTILE_HOSTNAMES=example.com alongside them (specs/002 quickstart.md).
public sealed class Turnstile(HttpClient http, TurnstileOptions options, ILogger<Turnstile> logger)
{
    // The widget's `action` on each form (frontend/src/screens/Login.tsx). A token
    // remembers which form it was solved on, so one solved on the reset form can't open
    // signup, and the other way round.
    public const string SignupAction = "signup";
    public const string PasswordResetAction = "password_reset";

    // Cloudflare's documented maximum. Anything longer can't be a real token, so it's
    // refused without spending a call on it.
    private const int MaxTokenLength = 2048;

    // True when the check is off, or Cloudflare accepts `token` as solved on the `action`
    // form at one of our hostnames. Fails closed: if Cloudflare can't be reached or answers
    // something unreadable, signup and reset wait until it's back, rather than the check
    // quietly switching itself off for whoever is sending the traffic that broke it.
    //
    // Nothing logged here includes the token or the client address. What is logged —
    // Cloudflare's error codes, the hostname and action a token claimed — is what an
    // operator needs to tell a misconfiguration from bots being turned away.
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, string action, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        // Form-encoded, as in Cloudflare's examples. remoteip is optional; when present,
        // Cloudflare can check the token was solved from the same address that sent it.
        // It's the address UseForwardedHeaders resolved, the same one the rate limits use.
        var form = new Dictionary<string, string>
        {
            ["secret"] = options.SecretKey!,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        SiteverifyResponse? result;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync("turnstile/v0/siteverify", content, cancellationToken);
            response.EnsureSuccessStatusCode();
            result = await response.Content.ReadFromJsonAsync<SiteverifyResponse>(cancellationToken);
        }
        // The three ways the call itself can fail: no connection or a non-2xx status
        // (HttpRequestException), the 10-second client timeout (TaskCanceledException
        // while the request's own token is still live — if *that* fired, the client went
        // away and the cancellation propagates as usual), and a body that isn't the JSON
        // we expect. Logged as a description, not the exception: its message could quote
        // the request.
        catch (HttpRequestException ex)
        {
            logger.LogError("Turnstile siteverify failed: HTTP {Status}.", ex.StatusCode?.ToString() ?? "no response");
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Turnstile siteverify failed: timed out.");
            return false;
        }
        catch (JsonException)
        {
            logger.LogError("Turnstile siteverify failed: unreadable answer.");
            return false;
        }

        if (result is not { Success: true })
        {
            logger.LogInformation("Turnstile token rejected: {ErrorCodes}.", string.Join(",", result?.ErrorCodes ?? []));
            return false;
        }

        if (result.Hostname is null || !options.Hostnames.Contains(result.Hostname))
        {
            logger.LogInformation("Turnstile token from an unlisted hostname: {Hostname}.", result.Hostname);
            return false;
        }

        // Cloudflare's test secrets answer without an action, and a real secret never gets
        // a test answer, so only those skip the action check.
        var testing = result.Metadata?.ResultWithTestingKey == true;
        if (result.Action != action && !testing)
        {
            logger.LogInformation("Turnstile token for another action: {Action}.", result.Action);
            return false;
        }

        return true;
    }

    // The parts of siteverify's answer this check reads. The names are Cloudflare's, hence
    // the attributes where they aren't camelCase.
    private sealed record SiteverifyResponse(
        bool Success,
        string? Hostname,
        string? Action,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes,
        SiteverifyMetadata? Metadata);

    private sealed record SiteverifyMetadata(
        [property: JsonPropertyName("result_with_testing_key")] bool? ResultWithTestingKey);
}
