# Feature Specification: Demo Mode

**Feature Branch**: `007-demo-mode`

**Created**: 2026-10-16

**Status**: Draft for owner review. Nothing here is approved for implementation until the owner reviews it (Principle VII). FR-004 relies on the Principle V amendment approved by the owner on 2026-10-16 (constitution 3.2.0).

**Input**: Owner request (2026-10-16): "It would be useful to have demo mode in the app so that user could see how it look like with data. Implement demo mode with made up data that looks real and covers all parts (templates, results, progress). How to reset demo mode?"

## Clarifications

### Session 2026-10-16

- Q: Where does the demo data live? → A: In a temporary demo account on the real backend, so every screen runs against the real API. Not a browser-only mock, and not a backup file to restore.
- Q: Does a demo account skip the privacy notice and optional-details consent? → A: No. The visitor goes through both like any new account.
- Q: Should the privacy notice mention demo accounts? → A: No. What a visitor types into a demo is their own choice, like anything typed into a search box, and the account is gone within a day.
- Q: How is the demo reset? → A: With a "Reset demo data" action shown only to demo accounts, which replaces the notebook with freshly generated data. Starting a new demo also gives fresh data, and every demo account is deleted automatically when it expires.
- Q: How does a visitor who allows optional details see the seeded title, gym, notes and bodyweight? → A: By resetting the demo after allowing them. The reset follows consent like restore does, and the screens hint at it (PR 2).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Try the app with a full notebook (Priority: P1)

As a visitor on the sign-in screen who has no account, I choose "Try the demo". After the bot check I'm signed in to a notebook that already looks like it belongs to someone who has trained for about three months: sessions to page back through, templates to start from and progress charts with a clear trend.

**Why this priority**: This is the whole point of the feature. Without it, there is no demo.

**Independent Test**: With demo mode enabled, start a demo from the sign-in screen. Without typing anything else, open Sessions, a session page, Templates, Progress for a weighted lift and Progress for a bodyweight exercise. Each one shows realistic data.

**Acceptance Scenarios**:

1. **Given** demo mode is enabled, **When** I start a demo and pass the bot check, **Then** I'm signed in to a new demo notebook without giving an email address or password.
2. **Given** a new demo notebook, **When** I open Sessions, **Then** I see roughly twelve weeks of finished sessions, newest first, the latest within the last few days, plus one session started today from a template that is still in progress.
3. **Given** a new demo notebook, **When** I open a finished session, **Then** it has several exercises in order, warm-up sets before working sets, and at least some sessions with skipped sets.
4. **Given** a new demo notebook, **When** I open Templates, **Then** there are three named templates, each with exercises and planned sets.
5. **Given** a new demo notebook, **When** I open Progress for a main lift, **Then** the chart rises over the weeks with ordinary session-to-session ups and downs and one visible lighter week, and it ends at the most recent session.
6. **Given** a new demo notebook, **When** I open Progress for Pull-up or Dips, **Then** the chart shows best reps, as for any bodyweight exercise.
7. **Given** a new demo notebook, **When** I meet the privacy notice and the optional-details question, **Then** they behave exactly as for a new registered account.
8. **Given** demo mode is disabled, **When** anyone tries to start a demo, **Then** nothing happens: the option isn't offered, and the server answers as if the feature didn't exist.

---

### User Story 2 - Reset the demo (Priority: P2)

As a visitor who has been changing the demo notebook (deleting sessions, editing templates, logging a test workout), I choose "Reset demo data", confirm, and get a clean demo notebook back.

**Why this priority**: A demo you can safely break is more useful to explore. It answers the owner's "how to reset" question, but the demo still works without it.

**Independent Test**: In a demo, delete two sessions, rename an exercise and add a template. Reset. The notebook matches a new demo again, dated relative to today.

**Acceptance Scenarios**:

1. **Given** a demo account, **When** I reset and confirm, **Then** every session, template and exercise in the notebook is replaced with freshly generated demo data.
2. **Given** a reset demo, **When** I open Progress, **Then** the chart still ends at the most recent session, not at the day the demo was first started.
3. **Given** a reset demo, **When** I look at the account, **Then** I'm still signed in, my privacy notice answer and consent answer are unchanged, and the expiry time is not extended.
4. **Given** a registered (non-demo) account, **When** a reset is requested for it, **Then** it is refused and nothing in the notebook changes.

---

### User Story 3 - Demo accounts clean themselves up (Priority: P2)

As the owner, I want demo accounts to disappear on their own so that demos never pile up in the database or become permanent accounts.

**Why this priority**: The feature is only safe to run in production if this exists, but it is invisible to visitors.

**Independent Test**: Create a demo account and move its expiry into the past. After the next cleanup run, the account and all its data are gone, and a token issued for it no longer works.

**Acceptance Scenarios**:

1. **Given** a demo account past its expiry, **When** the cleanup runs, **Then** the account and everything in its notebook are permanently deleted.
2. **Given** an expired demo account whose cleanup hasn't run yet, **When** its token is used, **Then** the request is refused as if signed out.
3. **Given** a registered account, **When** the cleanup runs, **Then** it is never touched.

---

### User Story 4 - Demo accounts cannot become real accounts (Priority: P3)

As the owner, I want a demo account to stay a demo, so it can't be used as a free account with no email address behind it.

**Independent Test**: In a demo, try a password change, an export and an account deletion, then try to sign in with the demo's internal address. Each one is refused.

**Acceptance Scenarios**:

1. **Given** a demo account, **When** anyone tries to sign in to it with an email address and password, **Then** the attempt fails in the same way as any wrong password.
2. **Given** a demo account, **When** the visitor tries to change the password, export the notebook or delete the account, **Then** the request is refused with a reason the app can explain.
3. **Given** a demo account, **When** its renewable session reaches the end of the demo's lifetime, **Then** it can't be renewed past the expiry.

### Edge Cases

- **Many demos at once**: once the number of live demo accounts reaches the configured cap, starting another is refused with a "try again later" answer, and no account is created.
- **One visitor starting many demos**: a per-address rate limit refuses further starts from the same network address within the window.
- **Failed bot check**: no account is created.
- **Seeding fails partway**: no demo account is left behind. A demo is created and filled as one unit, or not at all.
- **Reset racing other requests**: the reset waits for in-flight requests on the account and blocks new ones, the same way password change and deletion do, so no request sees a half-replaced notebook.
- **Reset after expiry**: refused like any other request from an expired account.
- **Optional details without consent**: the seeded titles, gym, notes and bodyweight follow the account's consent like any restore, so a demo started before consent has none. After the visitor allows optional details, a reset brings them in (FR-016).
- **Two demo starts in one browser**: the second demo replaces the first in that browser. The first account simply expires.
- **Seed always valid**: generated data obeys every rule real data obeys: sets on a finished session are done or skipped, sets on an in-progress session are planned or done, reps are at least 1, and names are unique per account.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Demo mode MUST be off unless the operator explicitly enables it. While it is off, the demo start and reset operations MUST answer as if they didn't exist.
- **FR-002**: A visitor MUST be able to start a demo from the sign-in screen without giving an email address, password or name. Starting MUST pass the same human check that registration uses, failing closed.
- **FR-003**: Starting a demo MUST create a new account of its own for that visitor, fill it with generated demo data and sign the visitor in, all as one operation that either completes or leaves nothing behind.
- **FR-004**: A demo account MUST be recognisable as a demo, MUST carry an expiry 24 hours after creation, and MUST count as already confirmed without any email being sent. *(Allowed by the Principle V amendment, constitution 3.2.0.)*
- **FR-005**: Demo starts MUST be limited per network address, and the number of unexpired demo accounts MUST be capped. Both limits MUST be configurable.
- **FR-006**: The generated data MUST cover every screen:
  - about twelve weeks of finished sessions on a repeating upper/lower routine, about three a week, with the latest within the last few days;
  - steady progression with realistic variation and one lighter (deload) week;
  - warm-up sets;
  - a few skipped sets;
  - at least two bodyweight exercises (Pull-up and Dips);
  - three templates;
  - one session started today from a template, in progress, with some sets done and the rest planned;
  - a title, gym, notes or bodyweight on some sessions.
- **FR-007**: The generated data MUST be dated relative to the day it is generated, so a new or reset demo always looks current.
- **FR-008**: The generated data MUST look like a real lifter's log: realistic weights in plate increments, realistic rep counts, exercise names that read naturally, and no placeholder text.
- **FR-009**: A demo account MUST go through the privacy notice and optional-details consent like any new account. Its seeded optional details MUST obey the same consent rules as restored data.
- **FR-010**: A demo account MUST be able to replace its whole notebook with freshly generated demo data (reset). The reset MUST keep the account, its session, its privacy answers and its expiry, MUST be coordinated with other requests on the account like other whole-account operations, and MUST be refused for non-demo accounts.
- **FR-011**: Expired demo accounts MUST be deleted automatically, with all their data, by the existing permanent deletion path. Requests made with an expired demo account's credentials MUST be refused before cleanup has run.
- **FR-012**: Password sign-in, password change, password reset, export and account deletion MUST be refused for demo accounts. Sign-in MUST fail exactly like a wrong password, so it reveals nothing.
- **FR-013**: A demo session MUST NOT be renewable past the demo's expiry.
- **FR-014**: The app MUST be able to tell a signed-in visitor that the account is a demo and when it expires, so the screens can show it (screens are delivered in the second PR).
- **FR-015**: Demo accounts MUST keep every existing ownership rule: one demo can never see another demo's or a registered user's data, and requests for another account's sessions or exercises answer "not found".
- **FR-016**: When a demo account has allowed optional details, a reset MUST include the seeded optional details. When it hasn't, they MUST be left out. The demo screens MUST tell the visitor that a reset loads them (second PR).

### Key Entities

- **Demo account**: an ordinary account marked as a demo, with an expiry time, an internal address that no one can receive mail at, and a password no one knows. It owns a notebook like any other account.
- **Demo notebook data**: generated sessions, exercises, sets and templates, produced fresh for each start or reset, dated relative to that day.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A visitor goes from the sign-in screen to a fully populated notebook in under 10 seconds, with at most one bot-check interaction.
- **SC-002**: In a new demo, every main screen (Sessions, a session page, Templates, a template, Progress for a weighted lift and Progress for a bodyweight exercise) shows non-empty, realistic content with no further input.
- **SC-003**: A reset returns the notebook to a new demo's state in under 5 seconds, without signing the visitor out.
- **SC-004**: No demo account outlives its expiry by more than one cleanup interval (at most 1 hour).
- **SC-005**: With demo mode off, no demo operation is reachable and registered accounts behave exactly as before.

## Assumptions

- The demo uses the existing account, session and notebook machinery. A demo is a real account with real data, so there is no second implementation of any screen's rules.
- The generated data is inserted through the existing restore path, so it obeys the same validation and consent rules as a user's backup.
- The demo lifetime (24 h), the per-address limit and the cap on live demos are operator settings with sensible defaults. Exact defaults are set in the plan.
- The demo's display name is a fixed, obviously made-up name (for example "Demo Lifter").
- The work is delivered in two PRs: the first covers backend behaviour and tests (FR-001–FR-016 server side), the second the screens (the start button, the demo badge with its expiry, reset and leave) and the documentation updates.

## Dependencies

- **Constitution Principle V amendment (approved by the owner on 2026-10-16, constitution 3.2.0)**: Principle V lists "email confirmation before an account can sign in" among the open-signup protections. A demo account signs in without one. The proposed amendment is that demo accounts, behind their own flag, human check, per-address limit, live-account cap and 24-hour expiry, are the one kind of account exempt from email confirmation, and they can't be used for password sign-in, export or deletion.
- The human check (Turnstile) and the existing per-address rate limiting.
- The existing restore path (`specs/004`) and the permanent deletion path (`specs/001`).
