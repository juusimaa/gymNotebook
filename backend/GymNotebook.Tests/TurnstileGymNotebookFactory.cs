using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Web;
using GymNotebook.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// Boots the app with the Turnstile check switched on (specs/002 tasks.md T051), and with
// Cloudflare replaced by StubSiteverifyHandler: the typed HttpClient Turnstile receives is
// built on the stub instead of a real network handler, so nothing leaves the process.
//
// A separate fixture for the same reason as PrivacyEnabledGymNotebookFactory: the shared
// GymNotebookFactory keeps the check off for every other test.
public class TurnstileGymNotebookFactory : GymNotebookFactory
{
    public const string SecretKey = "turnstile-test-secret-never-logged";

    // The frontend's hostname, and the one Cloudflare's test keys answer with — listed with
    // a space after the comma, so the split-and-trim in TurnstileOptions is exercised too.
    public const string Hostname = "app.example.test";
    public const string TestKeyHostname = "example.com";

    // Every form posted to the stub's siteverify, oldest first, shared by all the handler
    // instances the HttpClient factory creates.
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> SiteverifyRequests { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("TURNSTILE_SECRET_KEY", SecretKey);
        builder.UseSetting("TURNSTILE_HOSTNAMES", $"{Hostname}, {TestKeyHostname}");

        // ConfigureTestServices runs after Program.cs's registrations, so this adds to the
        // same named client ("Turnstile") and its primary handler wins. A new stub per
        // handler: the factory disposes handlers when it recycles them.
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<Turnstile>()
                .ConfigurePrimaryHttpMessageHandler(() => new StubSiteverifyHandler(SiteverifyRequests)));
    }
}

// Stands in for https://challenges.cloudflare.com/turnstile/v0/siteverify. The answer
// depends only on the token posted, so every test picks its scenario by the token it
// sends, and tests can run in any order without sharing a setting.
public sealed class StubSiteverifyHandler(ConcurrentQueue<IReadOnlyDictionary<string, string>> requests) : HttpMessageHandler
{
    // A token solved on the given form at our hostname.
    public const string PassSignup = "pass-signup";
    public const string PassReset = "pass-reset";
    // Cloudflare says no: expired, already used, or never issued.
    public const string Rejected = "rejected";
    // Valid, but solved on another site that uses the same site key.
    public const string OtherHostname = "other-hostname";
    // What Cloudflare's always-pass test secret answers: no action, a testing flag.
    public const string TestKey = "test-key";
    // Cloudflare can't be reached, or answers with an error status.
    public const string Unreachable = "unreachable";
    public const string ServerError = "server-error";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var parsed = HttpUtility.ParseQueryString(body);
        var form = parsed.AllKeys.OfType<string>().ToDictionary(key => key, key => parsed[key] ?? "");
        requests.Enqueue(form);

        return form.GetValueOrDefault("response") switch
        {
            PassSignup => Answer(new { success = true, hostname = TurnstileGymNotebookFactory.Hostname, action = Turnstile.SignupAction }),
            PassReset => Answer(new { success = true, hostname = TurnstileGymNotebookFactory.Hostname, action = Turnstile.PasswordResetAction }),
            OtherHostname => Answer(new { success = true, hostname = "elsewhere.example", action = Turnstile.SignupAction }),
            TestKey => Answer(new
            {
                success = true,
                hostname = TurnstileGymNotebookFactory.TestKeyHostname,
                action = "",
                metadata = new { result_with_testing_key = true },
            }),
            Unreachable => throw new HttpRequestException("No connection could be made."),
            ServerError => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => Answer(new Dictionary<string, object> { ["success"] = false, ["error-codes"] = new[] { "invalid-input-response" } }),
        };
    }

    private static HttpResponseMessage Answer(object json) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(json) };
}
