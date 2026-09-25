# UI Contract

Draft, 2026-09-24. Follow the existing privacy design preview in `docs/ui/README.md` and `docs/ui/prototype.html`; these are samples, not published legal content. Keep current typography, tokens, single-column layout, 44px controls and keyboard focus conventions.

## Routes and entry points

| Proposed route | Access | Behavior |
| --- | --- | --- |
| /privacy | Public | Current notice with version/effective date, disclosures, contact/rights and announced changes; returning to login records nothing |
| /account/privacy | Authenticated | Cover's “Privacy & account” entry; links to notice, export and deletion; available before notice acknowledgement |
| /account/privacy/notice | Authenticated | Current notice gate with “Continue”; separate sign-out/leave and privacy controls |
| /account/privacy/optional-details | Authenticated | Amendment 2026-09-25. The consent statement with "Allow" and "Not now" of equal prominence, or, when consent exists, its date and a "Withdraw" action. Reached from the account privacy screen and from the editor's opt-in entry |
| /account/export | Authenticated | Contents explanation, current password and download action |
| /account/delete | Authenticated | Consequences, optional export, cancellation, password and separate confirmation action |
| /account/deleted | Public, transient completion | Render actual successful response state; direct navigation without it shows a neutral signed-out state, never a deletion claim |

Login's shared sign-in/registration screen links to the public notice before submission. The cover exposes privacy within two actions and checks acknowledgement on “Open the notebook,” matching the preview. All notebook deep links (workouts, progress, exercises and editors) run the same gate before fetching/rendering notebook content. Cover/account/privacy/change-password remain accessible without acknowledging; opening these must not record acknowledgement.

## Notice transitions

Fetch account privacy state alongside authentication. Null/old acknowledgement routes attempted notebook entry to the gate. Continue sends exactly the displayed current version; only success opens the intended safe same-origin notebook route. Reject external return URLs. A 409 reloads the newer notice; a network failure retains the gate and offers retry. Leaving or signing out does not acknowledge. A successful same-version acknowledgement persists across sessions, not just browser memory. No accept-policy checkbox or consent language.

## Optional-details consent transitions

Amendment 2026-09-25 (spec Story 6, FR-029–FR-035).

- **Editor without consent:** the title, location, notes and bodyweight inputs are hidden. In their place is one labelled entry, "Add title, location, notes and bodyweight", which opens the consent screen and returns to the editor afterwards. Everything else in the editor works unchanged. The workout detail screen hides the same fields.
- **Grant:** "Allow" sends exactly the displayed statement version. A 409 reloads the newer statement. Success returns to the entry point with the inputs shown. "Not now", back or leaving records nothing.
- **Withdraw:** from the account privacy screen or the consent screen. A review step lists what is removed (all four fields on every workout), says the rest of the notebook stays, and offers export. It takes no more steps than granting and needs no password. Success shows the number of workouts cleared, and discards any open editor draft's details with a visible note. A lost response is retried; the result is the same.
- **Transition question:** when `transitionPending` is true, the notebook gate shows it after the notice gate and before any notebook fetch. It shows how many workouts hold details, with "Allow" and "Don't allow" of equal prominence, plus an export link. "Don't allow" goes through the same review step as withdrawal. Leaving keeps it pending for the next visit. Cover, account, privacy and change-password stay reachable, as with the notice gate.
- **Rejected save:** a 403 `optional_details_consent_required`, for example from a stale tab after withdrawal, keeps the draft, drops only its optional details with an explanation, and offers the opt-in entry. It is not a session-invalid event.
- **No pressure:** never a pre-ticked box, a nag on later visits for accounts without details, or copy suggesting the notebook needs consent.

## Export transitions

Idle → password verification/generation → receiving → complete, or actionable validation/authentication/recoverable failure. Explain that the one JSON file contains personal information, relationships and field explanations. Clear the password after submission/completion; never persist it. Show indeterminate progress when byte count is unknown. Only a complete response becomes a download; use a temporary browser object URL and revoke it when finished/cancelled. An empty account still downloads a valid file. Failure offers a fresh retry without changing notebook data.

## Deletion transitions

Review → separate password/confirmation action → deleting → confirmed completion, or failure/uncertain result. Show active data categories, irreversibility, all-session sign-out, optional export, backup deadline, 31-day minimal deletion evidence (the deletion log lines, data-model.md) and the reviewed restricted log exception before confirmation. Export is optional and never a prerequisite. Cancellation and incorrect password change nothing. Disable repeated submission while pending, but do not imply that closing the page after confirmed submission cancels server deletion.

Successful response clears local app-owned state and navigates to completion with only non-personal outcome metadata. A lost response/401 says the session no longer grants access and provides sign-in/contact options; it never certifies deletion. A known precommit failure offers retry. Unknown outcome explains uncertainty and the contact path without promising rollback.

## Coordination and suspension states (research R4 Q5, R6 Q2c)

- **Suspended account at login:** after a correct password, a 403 `account_suspended` shows neutral copy directing the user to the privacy contact. A wrong password keeps the existing generic login error, so the suspension is never shown to someone without the password.
- **Temporarily unavailable:** a 503 `temporarily_unavailable` shows a retry action, honouring `Retry-After` when present. Nothing was changed, so retrying is safe. Deletion uses the same copy for a lock timeout.
- **Write interrupted by sign-out:** a 401 on a write means the change may already have been saved. The re-sign-in state warns the user to check before repeating it, because a retried write can be duplicated.

## Invalidation and accessibility

On observed invalidation clear JWT, in-memory account/notebook/editor data, pending fetches and export object URLs; re-render signed-out UI. Same-origin tabs observe sign-out through the existing storage key/event pattern or an equivalent focused helper. Returning tabs and back/forward-cache restores must revalidate before rendering personal state. Password-verification 400 stays local to the form; it is not a session-invalid event. Offline devices can clear data only when invalidation is observed; downloaded files remain user controlled.

Every new route has loading, empty where relevant, error, retry and offline/network states. Use labelled password inputs, visible focus, logical tab order, accessible status/error announcements and focus restoration after navigation/failure. Do not trap focus or rely only on color. Completion/deletion messaging must remain understandable on mobile without exposing infrastructure implementation details.

The owner records mobile and keyboard walkthroughs of notice/export/deletion and of granting and withdrawing optional-details consent, each discoverable and completable without assistance in under three minutes excluding downloads. Automated helper tests, source review and HTTP checks cannot substitute for this acceptance evidence.
