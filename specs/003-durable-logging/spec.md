# Feature Specification: Durable Logging

**Feature Branch**: `003-durable-logging` (planning PR on `plan/milestone-13-durable-logging`)

**Created**: 2026-10-07

**Status**: Draft for owner review. Nothing here is approved for implementation until the owner merges this plan (Principle VII).

**Input**: The 2026-10-07 live design critique, priority issue P1 #3 ("mid-session continuity"). Sets reach the server only on **Save page** or **Finish session**, and the sign-in token lasts 30 minutes with no renewal. The "last time" part of that issue shipped separately in #124.

## Clarifications

### Session 2026-10-07

- Q: How should sign-in survive a long session? → A: A renewing token. A valid, unexpired token can be swapped for a fresh one; renewals stop at a fixed total session length. No refresh tokens, no new browser storage.
- Q: With saves every few seconds, two devices on one page can overwrite each other. Add a version check? → A: Yes. A stale save is refused, and the editor offers to reload.
- Q: Which pages save automatically? → A: In-progress sessions only: a new page and **Continue logging**. Corrections to a finished page keep the explicit **Save changes**.
- Q: Once a new page has saved itself, what does Cancel do? → A: It offers to tear the page out ("Tear out this page? Its 2 sets go with it."), or keep it as an in-progress session. Nothing is lost silently.
- Q: How long may renewals keep a session alive? → A: 12 hours from the password, for now (plan D5).
- Q: Does an in-progress page keep a Save button? → A: No. Autosave covers it, and less clutter at the rack. **Finish session** is the footer's only action there.
- Q: How long before an incomplete row is called out? → A: 60 seconds, so the notice doesn't nag between typing the weight and the reps.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sets are kept as I log them (Priority: P1)

As a lifter at the rack, I log a set and move on, and it is on the server within seconds, without me tapping Save. If my phone dies or I close the tab, the sets I logged are still there when I come back, on any device.

**Why this priority**: A paper log can't lose a line once it's written. This one can, until Save, and that breaks principle 4 ("Nothing is lost by accident").

**Independent Test**: On a new page, log two complete sets, wait for "Saved", close the tab without saving, sign in on another browser, and open the page from Sessions: both sets are there and the page is in progress.

**Acceptance Scenarios**:

1. **Given** a new page with no complete set, **When** I add an exercise, **Then** nothing is sent yet, and the page isn't in the sessions list.
2. **Given** a new page, **When** I complete its first set (weight and reps, or reps for a bodyweight block), **Then** within about two seconds the page exists on the server with that set, and the date line reads "Saved 09.42".
3. **Given** an in-progress page, **When** I change, add, remove or restore a set, **Then** the change is saved within about two seconds of the last edit.
4. **Given** a row with a weight but no reps, **When** autosave runs, **Then** the complete sets are saved and that row stays on screen unsaved; after it has been left like that for a minute, the date line says which set needs what ("Set 3 needs reps").
5. **Given** I'm offline or the server fails, **When** a save fails, **Then** the date line reads "Not saved — retrying", the edit stays on screen and in this tab's draft, and the save is retried after 5, 15 and then every 60 seconds until it works or I leave.
6. **Given** a finished page opened with **Edit**, **When** I change it, **Then** nothing saves until I tap **Save changes**, as today.

---

### User Story 2 - My sign-in lasts my session (Priority: P1)

As a lifter in a 75-minute session, I'm never sent to the sign-in screen mid-session just because 30 minutes have passed.

**Why this priority**: Today a long session's save is the request that finds the token expired. The draft survives, but signing in at the rack with chalky hands is exactly the friction the product exists to remove.

**Independent Test**: With a short token lifetime configured, keep using the editor past it; requests keep succeeding. Leave the app alone past the lifetime; the next request signs out as today.

**Acceptance Scenarios**:

1. **Given** I'm using the app, **When** my token is past half its lifetime and the app makes a request, **Then** the app first swaps it for a fresh one, and I notice nothing.
2. **Given** I come back to the tab after a rest, **When** the token is still valid but past half its lifetime, **Then** it is renewed as the tab becomes visible.
3. **Given** a session that started more than the renewal cap ago (about 12 hours), **When** renewal is attempted, **Then** it is refused, the current token runs out normally, and I sign in again.
4. **Given** I changed my password elsewhere, or reset it, **When** this browser tries to renew, **Then** renewal is refused and the session ends here as it does today.
5. **Given** two tabs open, **When** one renews the token, **Then** the other tab carries on with the new token without reloading. Signing out in one tab still signs out the other.
6. **Given** the app is open with nothing to save, **When** the token passes half its lifetime, **Then** it is renewed within a minute, without a request of mine.
7. **Given** I lock the phone or switch away, **When** the token is at least five minutes old, **Then** the app tries to renew it as the tab is hidden, so I come back with close to a full lifetime.

---

### User Story 3 - Two devices can't silently overwrite each other (Priority: P2)

As a lifter who started logging on my phone and opens the same page on a laptop, I'm told when the page has changed elsewhere instead of one device quietly replacing the other's sets.

**Independent Test**: Open one in-progress page in two browsers, log a set in each, and check that the second save is refused with a reload offer, and nothing is lost.

**Acceptance Scenarios**:

1. **Given** the page was saved from another device since I opened it, **When** my editor saves, **Then** the save is refused, the date line says "This page changed on another device", and I'm offered **Reload page**. My unsaved edits stay in this tab's draft until I choose.
2. **Given** I choose **Reload page**, **Then** the editor shows the server's page, and the edits that weren't saved are listed in the notice ("Not saved here: Bench Press set 4 · 80 kg × 5"), so nothing vanishes without a trace.

---

### User Story 4 - Leaving a new page is honest about what happens (Priority: P2)

As a lifter who started a page by mistake, I can get rid of it, and I can't lose a real session by tapping Cancel.

**Acceptance Scenarios**:

1. **Given** a new page that has saved itself, **When** I tap **Cancel**, **Then** an inline confirmation asks "Tear out this page? Its 2 sets go with it." with **Tear out page** and **Keep page**. Keep leaves for the session list with the page there, in progress.
2. **Given** a new page that hasn't saved anything yet, **When** I tap **Cancel**, **Then** it behaves as today (discard the local draft, with the existing confirmation when sets were typed).

### Edge Cases

- A save is in flight when another edit lands: the edit waits and goes in the next save; saves never overlap or arrive out of order.
- The token expires during a failed-save retry loop (renewal impossible, for example offline for 40 minutes): the draft is held as today, and after signing in again the retry resumes against the same page.
- A restored draft (reload, or sign-in after expiry) on an in-progress page saves itself once restored, like any edit, and follows the version check.
- **Finish session** flushes any pending save first, then asks its question (#118).
- An exercise typed as new is created by the first save that includes it; its bodyweight choice is saved with it, as today.
- A page deleted on another device: the save gets 404, the date line says the page no longer exists, and the draft is kept until I leave so I can copy anything I need.

## Requirements *(mandatory)*

### Functional Requirements

**Autosave**

- **FR-001**: On an in-progress page (a new page, or an edit page without a finish time), the editor saves the page automatically: about 2 s after the last edit, and immediately when a set row becomes complete.
- **FR-002**: An autosave sends only complete sets. Incomplete rows stay on screen and in the tab's draft, unsaved.
- **FR-003**: A new page is created on the server by its first autosave that has a complete set, and not before. After that, the editor's address becomes that page's edit address (`/workouts/{id}/edit`, replacing the history entry), so a reload or **Continue logging** opens the same page.
- **FR-004**: At most one save is in flight per editor. Edits made during a save are sent in the next one.
- **FR-005**: The date line shows the save state as a polite live region: "Saving…", "Saved HH.mm", "Not saved — retrying", "Set N needs reps/weight", "This page changed on another device" (with **Reload page**), "This page no longer exists".
- **FR-006**: A failed save is retried after 5 s, 15 s, then every 60 s while the editor is open; any new edit retries at once.
- **FR-007**: An in-progress page has no Save button: **Finish session** is the footer's only action, and it flushes any pending save before it confirms. A finished page's editor is unchanged: explicit **Save changes**, no autosave.
- **FR-008**: The tab's draft in sessionStorage stays, as the copy of what the server hasn't confirmed yet (docs/privacy P5 unchanged).

**Version check**

- **FR-009**: Every workout carries a revision number. Each write to the page or its sets (`PATCH /workouts/{id}`, `PUT /workouts/{id}/exercises`, the set routes) increases it by one, and every workout response returns it.
- **FR-010**: A write may send the revision it was based on. If the page has moved on since, the write is refused with `409 page_changed`, and nothing is written. A write without one is accepted as today.
- **FR-011**: The editor always sends the revision it holds.

**Token renewal**

- **FR-012**: `POST /auth/token` swaps the caller's valid, unexpired bearer token for a fresh one with the configured lifetime. It is refused if the session started more than `Jwt:RenewalCapHours` ago (default 12), if the token's version claim is stale, if the account is suspended or unconfirmed, and for link tokens (as every bearer route already does).
- **FR-013**: Sign-in, password change and password reset start a new session: their tokens record the time the password was proven. A renewed token keeps that time, so renewal can't extend a session past the cap. A token issued before this change has no such time and can't be renewed; it runs out normally.
- **FR-014**: The frontend renews when the token is past half its lifetime: before its next request, when the tab becomes visible, and on a one-minute check while the tab is visible. As the tab is hidden it also tries to renew any token at least five minutes old, without delaying requests sent on the same event. Only one shared renewal runs at a time per tab.
- **FR-015**: Another tab replacing the token with one for the same account doesn't reload or sign out this tab. A removed token, or a token for another account, still does (today's behaviour).
- **FR-016**: Renewal uses the per-IP `auth` rate limit.

### Key Entities

- **Workout** (changed): gains `Revision` (integer, starts at 1).
- **Access token** (changed): gains the session start time (`auth_time`), kept across renewals.

## Success Criteria *(mandatory)*

- **SC-001**: In a 90-minute session logged on a phone, the lifter never taps Save and never sees the sign-in screen (owner walkthrough).
- **SC-002**: A set completed on a phone is on the server within 3 s on a working connection (automated test of the debounce plus owner walkthrough).
- **SC-003**: No interleaving of two devices' saves on one page loses a saved set without the editor saying so (automated tests).
- **SC-004**: Renewal never extends a session past the cap, and never survives a password change (automated tests).

## Out of scope

- Offline-first logging (a service worker or IndexedDB queue). Failed saves retry while the tab is open; the sessionStorage draft covers reloads, as today.
- Refresh tokens or cookies of any kind.
- Merging two devices' concurrent edits automatically.
- Autosave on finished pages.
- Per-set timestamps (and so inferring the end time from the last set, which #118 also left out).

## Assumptions and dependencies

- The privacy record (docs/privacy/processing-decision.md P5) needs no change: no new browser storage; the token keeps its key; the draft keeps its keys and contents.
- The constitution's Principle V holds: bearer JWT authentication and token-version revocation are kept; renewal adds no new credential.
- Deployed with the same configuration as today plus `Jwt__RenewalCapHours` (optional, default 12).
