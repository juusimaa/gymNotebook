# Feature Specification: Email Login and Open Signup

**Feature Branch**: `002-email-login` (planning PR on `plan/milestone-12-email-login`)

**Created**: 2026-10-06

**Status**: Done (2026-10-07), implemented in PRs #107–#110. *Originally:* Draft for owner review. Requires the constitution 3.0.0 amendment in the same PR (Principle V, Q2). Nothing here is approved for implementation until the owner merges this plan (Principle VII).

**Input**: User description: "Plan a new milestone for email login and disabling invite code. Take a look at the docker-subscription-tracker project how it is done and I think we could do it the same way. Only thing is that this is a mobile-only web app."

**Reference implementation**: `docker-subscription-tracker` milestone 9 (`b23e701`, password reset and email verification) and its open-signup change (`effd8ef`: confirm before sign-in, no enumeration, email caps, Turnstile). That project is FastAPI; the design carries over and the code is re-written in this project's .NET/React idiom.

## Clarifications

### Session 2026-10-06

- Q: How do existing username-only accounts move to email? → A: They don't. Nobody but the owner has used the service, so every existing account and its notebook is wiped before the email schema ships. No transition gate and no username sign-in period.
- Q: What happens to usernames? → A: Kept as a display name (the name printed on the notebook cover). It no longer signs anyone in and is no longer unique.
- Q: Does this wait for milestone 11 (specs/001) to go live? → A: No. Milestone 11's remaining work is owner/operator release gates, so this milestone proceeds now. The privacy documents that specs/001 maintains are updated in this milestone so they describe email before that feature's flag is switched on.
- Q: What is the display name called in code? → A: `DisplayName` (`display_name`, `displayName`); the `Username` column is renamed.
- Q: May email be collected before specs/001's privacy notice is live in production? → A: Yes, owner-accepted interim, as for optional details.
- Q: Where does the email provider send from? → A: Resend's EU region, Ireland (`eu-west-1`).
- Q: Magic link, one-time code, or password? → A: Password, with the email address as the identifier, the same as the reference implementation. Email is used to prove the address and to reset a forgotten password, not to sign in.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create an account with my email (Priority: P1)

As a new visitor on my phone, I enter my email, a password and the name for my notebook's cover, then confirm my address from the email I receive, so I can start a notebook without an invite code.

**Why this priority**: Removing the invite code is the point of the milestone, and it is only safe once an address has to be proven before the account can be used.

**Independent Test**: Register through the API with the memory email backend, read the confirmation link out of the captured message, confirm, and sign in. Repeat with an already-registered address and check the API answer is byte-for-byte the same.

**Acceptance Scenarios**:

1. **Given** a new address, **When** I submit the signup form, **Then** I see a "check your inbox" screen and receive a confirmation link valid for 48 hours.
2. **Given** an unconfirmed account, **When** I sign in with the correct password, **Then** I am told to confirm my address first and can resend the link; with a wrong password I get the ordinary "invalid email or password" answer.
3. **Given** a confirmation link, **When** I open it on my phone (signed in or not, in any browser), **Then** the address is confirmed and I am offered sign-in with my email pre-filled. Opening it a second time still says confirmed.
4. **Given** an address that already has a confirmed account, **When** someone signs up with it, **Then** the screen is the same as for a new address, and the inbox owner receives a "you already have an account" email with no link that changes anything. The existing password is untouched.
5. **Given** an address with an unconfirmed account, **When** someone signs up with it again, **Then** the screen is the same, and the inbox owner receives a "finish creating your account" email whose link lets them choose the password. The first registrant's password never becomes usable.

---

### User Story 2 - Sign in with my email (Priority: P1)

As a returning user, I sign in with my email and password.

**Independent Test**: Sign in with confirmed, unconfirmed, suspended, unknown and wrong-password cases and compare statuses and bodies.

**Acceptance Scenarios**:

1. **Given** a confirmed account, **When** I sign in with the address in any letter case and with surrounding spaces, **Then** I am signed in.
2. **Given** an unknown address or a wrong password, **When** I sign in, **Then** both get the same 401.
3. **Given** a suspended account (specs/001 R6), **When** I sign in with the correct password, **Then** suspension is reported as today, whether or not the address is confirmed.

---

### User Story 3 - Reset a forgotten password (Priority: P1)

As a user who forgot my password, I request a reset link from the sign-in screen and choose a new password on my phone.

**Independent Test**: Request a reset for a known and an unknown address (same answer, mail only for the known one), use the link, check the old password and every old session stop working, and the link cannot be used twice.

**Acceptance Scenarios**:

1. **Given** any address, **When** I request a reset, **Then** I see "If there's an account for that address, we've sent a link", and an email goes only to a real account.
2. **Given** a reset link younger than one hour, **When** I choose a new password, **Then** I am signed in in that browser, every other session is signed out, and the address counts as confirmed.
3. **Given** a used or expired link, **When** I open it, **Then** I am told it no longer works and can request a new one.
4. **Given** a suspended account, **When** its reset link is used, **Then** the password does not change and the suspension answer is shown.

---

### User Story 4 - Abuse resistance for an open signup (Priority: P1)

As the operator, I can open signup on a public URL without letting a script fill the database, use up the email quota, or learn which addresses have accounts.

**Independent Test**: The caps, Turnstile and rate-limit tests below, plus a review of every new route's answers for enumeration.

**Acceptance Scenarios**:

1. **Given** Turnstile is configured, **When** signup or reset arrives without a passing token for the expected form and hostname, **Then** it is refused with 400 `captcha` and nothing is sent. If Cloudflare cannot be reached, it is refused (fail closed).
2. **Given** one address has had five emails in the last 24 hours, **When** another is due, **Then** it is not sent and the API answer is unchanged.
3. **Given** the service has sent `EMAIL_DAILY_CAP` emails in the last 24 hours, **When** another is due, **Then** it is not sent, the answer is unchanged, and a warning is logged without the address.

### Edge Cases

- **The link opens in a different browser.** On a phone, a mail app may open links in its own in-app browser, or in Safari/Chrome when the user normally uses the app from the home screen. Confirmation therefore never depends on a session and ends on "confirmed — sign in". A reset signs in only the browser that opened the link; the home-screen app still needs a sign-in, and the screen says so.
- **Mail apps prefetch or scan links.** A security scanner that opens the confirmation link confirms the address, which is acceptable: it can only happen to mail delivered to that inbox. The link page posts the token from script rather than confirming on GET, so a scanner that doesn't run script changes nothing. The reset link only shows a form; nothing changes until the user submits it.
- **Two signups for one address at once.** The unique index decides; the loser is handled as "existing account".
- **Provider down.** Sending happens after the response; a failure is logged (without the address) and the user can resend.
- **Address typo.** The account stays unconfirmed and unusable; nothing is leaked to the mistyped address beyond a confirmation link.

## Requirements *(mandatory)*

### Functional Requirements

**Accounts and sign-in**

- **FR-001**: An account is identified by its email address, stored trimmed and lowercased, unique across accounts.
- **FR-002**: Signup takes email, password and a display name (shown on the cover). The display name is required, trimmed, not unique, and never used to sign in.
- **FR-003**: Sign-in takes email and password. Unknown address and wrong password produce the same 401. After a correct password, a suspended account gets 403 `account_suspended` (unchanged), otherwise an unconfirmed account gets 403 `email_not_verified`.
- **FR-004**: A bearer token belonging to an unconfirmed account is rejected (401) on every authenticated route.
- **FR-005**: Password rules are unchanged from today's register and change-password, with an added 72-byte upper bound (BCrypt ignores anything longer).

**Links**

- **FR-006**: Confirmation and reset links are signed, expiring tokens with a purpose: confirmation 48 hours, reset 1 hour. Neither is accepted as a bearer token, and neither is accepted for the other purpose.
- **FR-007**: A reset link works once. Completing a reset revokes every existing session of the account.
- **FR-008**: Completing a reset confirms the address, so someone who forgets their password before confirming is not locked out.
- **FR-009**: Link errors are reported as 400 `expired` or `invalid`, never 401, because the frontend treats every 401 as "session ended".
- **FR-010**: Links carry the token in the URL fragment (`/verify-email#token=…`, `/reset-password#token=…`), so it is never sent to a server, and the screen removes it from the address bar and history as soon as it has read it.

**No enumeration**

- **FR-011**: Signup always answers 202 with no body. What differs (new / confirmed / unconfirmed account) goes only to the inbox, per Story 1 scenarios 1, 4 and 5.
- **FR-012**: Resend confirmation takes email and password and always answers 204; it sends only when the password is correct and the account is unconfirmed.
- **FR-013**: Reset request always answers 202.
- **FR-014**: Where one branch hashes a password and another doesn't, the other branch performs an equivalent BCrypt hash so response timing does not reveal the branch. Email is sent after the response, so provider latency doesn't either.

**Email delivery and limits**

- **FR-015**: At most 5 emails per address per rolling 24 hours and at most `EMAIL_DAILY_CAP` (default 90, under the provider's free 100/day) in total. A capped send is silent to the caller.
- **FR-016**: The send log stores only a keyed hash of the address and the time, and keeps rows no longer than 24 hours.
- **FR-017**: Emails are English, plain text plus minimal HTML, no images, no tracking pixels, no click tracking.
- **FR-018**: Outside Development, the app refuses to start unless a real email backend and its key are configured, and never writes a link or token to a log.

**Human check and rate limits**

- **FR-019**: Signup and reset request require a Cloudflare Turnstile token when `TURNSTILE_SECRET_KEY` is set. Verification checks success, hostname and the form's action, and fails closed. Sign-in has no captcha: it needs a password and is rate limited.
- **FR-020**: Per-IP rate limits: the existing `auth` policy on sign-in and signup; resend and reset request 5 per hour; link confirmation 10 per hour.

**Removals and data**

- **FR-021**: The invite code is removed everywhere: request field, configuration, Bicep secret, deploy parameter, tests, UI, documents.
- **FR-022**: Every account existing before the email schema is deleted, with its notebook, by an operator step recorded in the operations checklist. The migration that adds the required email column must fail on a non-empty users table rather than invent addresses.
- **FR-023**: The notebook export includes the email address and confirmation time, each documented in the export field guide.
- **FR-024**: Account deletion removes the address with the account. The send log holds no address, so nothing is left to delete there.

**Mobile**

- **FR-025**: All new screens follow the existing mobile layout: one column, 16px gutters, 44px touch targets, 16px input text. Email fields use `type="email"`, `autocomplete="username"`, no auto-capitalisation or auto-correct; new-password fields use `autocomplete="new-password"`. The Turnstile widget uses the flexible size so it fits a 320px screen.

### Key Entities

- **User** (changed): adds `Email` (required, unique, lowercase) and `EmailVerifiedAt` (null until confirmed). `Username` is renamed `DisplayName`; its unique index is dropped. See [data-model.md](data-model.md).
- **EmailSend** (new): one row per sent or attempted email — keyed hash of the recipient, timestamp. Used only for caps.

## Success Criteria *(mandatory)*

- **SC-001**: A new user can go from the sign-in screen to a confirmed, signed-in account on a phone in under three minutes, excluding email delivery time (owner walkthrough).
- **SC-002**: No API answer on signup, resend, reset request or sign-in differs between an address that has an account and one that doesn't, other than after a correct password (automated tests).
- **SC-003**: A script cannot cause more than `EMAIL_DAILY_CAP` emails per day or five to one address (automated tests).
- **SC-004**: Production runs with no `INVITE_CODE` setting anywhere, and Turnstile configured (operations checklist).

## Out of scope

- Changing the email address of an existing account.
- Magic-link or one-time-code sign-in; refresh tokens.
- Emails in any language but English; account-activity or deletion notification emails.
- A local mail catcher (Mailpit). Development reads the message from the API's console output.
- Moving the domain's HTTP traffic behind Cloudflare's proxy.

## Assumptions and dependencies

- Resend's free tier (3,000/month, 100/day) is enough, sending from its EU region (Ireland, `eu-west-1`). Its supplier review (terms, DPA, data location) is an operator task before go-live, recorded in `docs/privacy/suppliers.md`.
- A sending subdomain of `gymnotebook.fit` gets SPF/DKIM records and the apex a DMARC record; DNS stays at Cloudflare.
- Turnstile adds Cloudflare as a recipient of the visitor's IP and browser signals on the signup and reset screens only. That is a change to the supplier inventory and the notice candidate, made in this milestone.
- Until specs/001's flag is on in production, no privacy notice is shown at signup. The owner accepted collecting email in that interim on 2026-10-06, as already accepted for optional details (specs/001 amendment 2026-09-25).
