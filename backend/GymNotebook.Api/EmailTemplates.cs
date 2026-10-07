using System.Net;

namespace GymNotebook.Api;

// The four emails the app sends (specs/002 spec Stories 1 and 3, contracts/api.md →
// "Register: what the inbox gets"), as plain C#: a template engine would be a dependency
// for four short messages. Each has a plain-text part and a minimal HTML part with the
// same words; the HTML is inline-styled, since mail clients drop most stylesheets, and has
// no images and no tracking pixel.
//
// Links point at the frontend (APP_URL) and carry the token after "#" (spec FR-010):
// browsers never send the fragment to a server or put it in a Referer header, so the token
// can't end up in a proxy or access log on its way to the page that reads it.
//
// The tokens themselves come from the caller (PR 3 adds JwtTokenFactory.CreateLinkToken);
// these methods only build messages. Singleton: it holds nothing but the options.
public sealed class EmailTemplates(EmailOptions options)
{
    // The design tokens (frontend/src/styles/tokens.css) as literal colors — mail clients
    // don't do CSS variables. Georgia is the web font stack's own fallback for Lora.
    private const string Ink = "#201f1d";
    private const string Paper = "#f3f2f2";
    private const string Muted = "#605d5d";

    // Spec Story 1, scenario 1: a new account was created, unconfirmed.
    public EmailMessage Confirmation(string to, string verifyToken)
    {
        var link = Link("verify-email", verifyToken);
        return Build(
            kind: "confirmation",
            to: to,
            subject: "Confirm your email for Gym Notebook",
            lead: $"Confirm that {to} is your address for Gym Notebook.",
            link: link,
            button: "Confirm email",
            rest:
            [
                "The link works for 48 hours. Once your address is confirmed, you can sign in, and reset your password by email if you ever forget it.",
                "If you didn't create a Gym Notebook account, ignore this email. Nothing will happen.",
            ]);
    }

    // Story 1, scenario 4: someone signed up with an address that already has a confirmed
    // account. Signup answers the same either way, so this email is the only place that
    // says the account exists — and only the inbox's owner reads it. No token: it signs
    // nobody in, it only points at the sign-in screen.
    public EmailMessage AlreadyRegistered(string to)
    {
        var link = $"{options.AppUrl}/";
        return Build(
            kind: "already_registered",
            to: to,
            subject: "You already have a Gym Notebook account",
            lead: $"Someone tried to create a Gym Notebook account for {to}, but you already have one.",
            link: link,
            button: "Sign in",
            rest:
            [
                "If that was you, sign in instead. If you've forgotten your password, choose \"Forgot password?\" on the sign-in screen.",
                "If it wasn't you, ignore this email. Nothing about your account has changed.",
            ]);
    }

    // Story 1, scenario 5: someone signed up with an address whose account exists but was
    // never confirmed. The link is a *reset* link, not a confirmation: whoever reads this
    // inbox chooses the password, so an account a stranger opened with this address can't
    // be confirmed with the stranger's password still on it.
    public EmailMessage FinishSignup(string to, string resetToken)
    {
        var link = Link("reset-password", resetToken);
        return Build(
            kind: "finish_signup",
            to: to,
            subject: "Finish creating your Gym Notebook account",
            lead: $"Finish creating your Gym Notebook account for {to} by choosing a password.",
            link: link,
            button: "Choose a password",
            rest:
            [
                "This address was used to sign up before, but never confirmed. The link works once, for 1 hour, and confirms your address too.",
                "If you didn't sign up, ignore this email. Nothing will happen.",
            ]);
    }

    // Story 3: a password reset was requested for an existing account.
    public EmailMessage PasswordReset(string to, string resetToken)
    {
        var link = Link("reset-password", resetToken);
        return Build(
            kind: "password_reset",
            to: to,
            subject: "Reset your Gym Notebook password",
            lead: $"Someone asked to reset the Gym Notebook password for {to}.",
            link: link,
            button: "Choose a new password",
            rest:
            [
                "The link works once, for 1 hour. Setting a new password signs you out on every other device.",
                "If you didn't ask for this, ignore this email. Your password has not changed.",
            ]);
    }

    // "/verify-email#token=..." on the frontend. A JWT is already URL-safe (base64url and
    // dots), so the escaping is a guard, not a transformation.
    private string Link(string route, string token) =>
        $"{options.AppUrl}/{route}#token={Uri.EscapeDataString(token)}";

    private static EmailMessage Build(string kind, string to, string subject, string lead, string link, string button, string[] rest)
    {
        // Text part: the lead, the bare link on its own line (clients make it clickable),
        // then the rest.
        var text = string.Join("\n\n", [lead, link, .. rest]);

        // Every interpolated value is HTML-encoded: the address is user input and must not
        // be able to inject markup into a message sent in the app's name.
        static string Paragraph(string value) => $"<p style=\"margin:0 0 16px\">{WebUtility.HtmlEncode(value)}</p>";
        var href = WebUtility.HtmlEncode(link);
        var html =
            $"<div style=\"font-family:Georgia,'Times New Roman',serif;font-size:16px;line-height:24px;color:{Ink};max-width:520px\">"
            + Paragraph(lead)
            + $"<p style=\"margin:24px 0\"><a href=\"{href}\" style=\"display:inline-block;background:{Ink};color:{Paper};padding:12px 20px;text-decoration:none;font-weight:bold\">{WebUtility.HtmlEncode(button)}</a></p>"
            + string.Concat(rest.Select(Paragraph))
            + $"<p style=\"margin:0;font-size:13px;color:{Muted}\">Or paste this link into your browser:<br>{href}</p>"
            + "</div>";

        return new EmailMessage(kind, to, subject, text, html);
    }
}
