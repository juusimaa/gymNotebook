# UI Contract

Draft, 2026-09-24. Follow the existing privacy design preview in `docs/ui/README.md` and `docs/ui/prototype.html`; these are samples, not published legal content. Keep current typography, tokens, single-column layout, 44px controls and keyboard focus conventions.

## Routes and entry points

| Proposed route | Access | Behavior |
| --- | --- | --- |
| /privacy | Public | Current notice with version/effective date, disclosures, contact/rights and announced changes; returning to login records nothing |
| /account/privacy | Authenticated | Cover's “Privacy & account” entry; links to notice, export and deletion; available before notice acknowledgement |
| /account/privacy/notice | Authenticated | Current notice gate with “Continue”; separate sign-out/leave and privacy controls |
| /account/export | Authenticated | Contents explanation, current password and download action |
| /account/delete | Authenticated | Consequences, optional export, cancellation, password and separate confirmation action |
| /account/deleted | Public, transient completion | Render actual successful response state; direct navigation without it shows a neutral signed-out state, never a deletion claim |

Login's shared sign-in/registration screen links to the public notice before submission. The cover exposes privacy within two actions and checks acknowledgement on “Open the notebook,” matching the preview. All notebook deep links (workouts, progress, exercises and editors) run the same gate before fetching/rendering notebook content. Cover/account/privacy/change-password remain accessible without acknowledging; opening these must not record acknowledgement.

## Notice transitions

Fetch account privacy state alongside authentication. Null/old acknowledgement routes attempted notebook entry to the gate. Continue sends exactly the displayed current version; only success opens the intended safe same-origin notebook route. Reject external return URLs. A 409 reloads the newer notice; a network failure retains the gate and offers retry. Leaving or signing out does not acknowledge. A successful same-version acknowledgement persists across sessions, not just browser memory. No accept-policy checkbox or consent language.

## Export transitions

Idle → password verification/generation → receiving → complete, or actionable validation/authentication/recoverable failure. Explain that the one JSON file contains personal information, relationships and field explanations. Clear the password after submission/completion; never persist it. Show indeterminate progress when byte count is unknown. Only a complete response becomes a download; use a temporary browser object URL and revoke it when finished/cancelled. An empty account still downloads a valid file. Failure offers a fresh retry without changing notebook data.

## Deletion transitions

Review → separate password/confirmation action → deleting → confirmed completion, or failure/uncertain result. Show active data categories, irreversibility, all-session sign-out, optional export, backup deadline, 31-day minimal suppression evidence and the reviewed restricted log exception before confirmation. Export is optional and never a prerequisite. Cancellation and incorrect password change nothing. Disable repeated submission while pending, but do not imply that closing the page after confirmed submission cancels server deletion.

Successful response clears local app-owned state and navigates to completion with only non-personal outcome metadata. A lost response/401 says the session no longer grants access and provides sign-in/contact options; it never certifies deletion. A known precommit failure offers retry. Unknown outcome explains uncertainty and the contact path without promising rollback.

## Invalidation and accessibility

On observed invalidation clear JWT, in-memory account/notebook/editor data, pending fetches and export object URLs; re-render signed-out UI. Same-origin tabs observe sign-out through the existing storage key/event pattern or an equivalent focused helper. Returning tabs and back/forward-cache restores must revalidate before rendering personal state. Password-verification 400 stays local to the form; it is not a session-invalid event. Offline devices can clear data only when invalidation is observed; downloaded files remain user controlled.

Every new route has loading, empty where relevant, error, retry and offline/network states. Use labelled password inputs, visible focus, logical tab order, accessible status/error announcements and focus restoration after navigation/failure. Do not trap focus or rely only on color. Completion/deletion messaging must remain understandable on mobile without exposing infrastructure implementation details.

The owner records mobile and keyboard walkthroughs of notice/export/deletion, each discoverable and completable without assistance in under three minutes excluding downloads. Automated helper tests, source review and HTTP checks cannot substitute for this acceptance evidence.
