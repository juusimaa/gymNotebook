# UI Contract: Email Login and Open Signup

Draft for owner review, 2026-10-06. Mobile only: every screen is the existing one-column layout (16px gutters, 44px touch targets, 16px input text, existing tokens and classes from `src/styles/`). `docs/ui/README.md` and `docs/ui/prototype.html` are updated in the PR that ships each screen.

## `/login` (changed)

Today's single form with two actions becomes three modes on one route, switched in place, not separate pages, so the browser's back button doesn't step through them:

- **Sign in**: Email, Password, "Sign in". Below the password: "Forgot your password?". Below the form: "New here? Create an account".
- **Create account**: Email, Password, "Name on the cover", Turnstile widget, "Create account". The specs/001 service description stays beside this action. No invite field.
- **Forgot password**: Email, Turnstile widget, "Send link".

Result states, still on `/login`:

- **Check your inbox** (after create account, or after a 403 `email_not_verified` on sign-in): names the address, explains the link lasts 48 hours, "Send the link again" (calls resend with the email and password still held in memory from the form; the password is dropped when the screen is left).
- **Reset sent**: "If there's an account for *address*, we've sent a link. It works for one hour."

Field attributes: email `type="email"`, `autocomplete="username"`, `autocapitalize="off"`, `autocorrect="off"`, `spellcheck="false"`, `inputmode="email"`. Password on sign-in `current-password`, on create `new-password`. Name `autocomplete="nickname"`.

Error copy (`api/authErrors.ts`): 401 "Email or password is wrong"; 403 `email_not_verified` → Check-your-inbox state, not an error line; 400 `captcha` "The check didn't pass. Try again."; 429 as today.

## `/verify-email#token=…` (new, public)

Reads the token from the fragment once, removes it from the address bar, posts it.

- Success: "*address* is confirmed." with a "Sign in" button that opens `/login` with the email filled in. If a session already exists in this browser, the button reads "Open my notebook" instead.
- `expired`: "This link has expired." + a short path to sign in, where the unconfirmed sign-in offers a fresh link.
- `invalid` or no token: "This link doesn't work." + "Go to sign in".

## `/reset-password#token=…` (new, public)

Reads and removes the token; doesn't post anything until submit.

- Form: New password, Repeat new password (`new-password`), mismatch caught before sending, "Set password".
- Success: stores the returned token and navigates into the notebook (through the existing guards). A one-line note says other devices and the home-screen app are signed out.
- `expired`/`invalid`: "This link no longer works" + "Send a new link" (opens `/login` in Forgot mode with the reason shown).
- 403 `account_suspended`: the existing suspension copy.

## Turnstile widget

A small component that loads `https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit` only on the two forms that need it, renders with `size: "flexible"`, `theme: "auto"` (follows the dark theme from #105), `action` per form, and remounts after every submit attempt (tokens are single-use). Submit stays disabled until a token exists. When no site key is configured, the component renders nothing and the API isn't sent a token.

## Cover

Shows the display name as today, with the signed-in email address beside the account links (read-only), so the user can tell which account this browser is in.
