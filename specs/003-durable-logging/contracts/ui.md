# UI Contract: Durable Logging

Only the session editor changes on screen. All copy is proposed and open to review.

## Save state in the date line

The date line ("Today 09.34 · add details") gains the save state, as a polite live region. It never moves focus. *As built (PR 5):* the state sits on its own line directly under the dateline rather than inside it, because the dateline is a button and a live region inside it would change the button's name; the incomplete-row notice names the exercise, since "Set 3" alone could be in any block; and a heading the autosave can't send reads "Not saved: Enter a valid date and start time." in `--color-error`.

| State | Text | Style |
| --- | --- | --- |
| Nothing to save yet | (nothing) | |
| Waiting / saving | "Saving…" | muted |
| Saved | "Saved 09.42" | muted |
| Failed, retrying | "Not saved — retrying" | `--color-error` |
| Incomplete row for 60 s | "Bench Press set 3 needs reps" (or "weight") | muted |
| Conflict | "This page changed on another device." + **Reload page** (44px text button) | `--color-error` |
| Deleted elsewhere | "This page no longer exists." | `--color-error` |

## Footer

- **In-progress page** (new, or Continue logging): only **Finish session** (primary, full width), which flushes any pending save, then confirms as in #118. No Save button: autosave covers it (owner, 2026-10-07). *As built:* the edit page of a session in progress has no Finished field either, since a time typed there would never be saved; Finish session is how it ends.
- **After a conflict**, Cancel on a new page asks the ordinary discard question rather than offering to tear the page out, which would delete the other device's sets too.
- **Finished page**: unchanged, explicit **Save changes**.

## Cancel on a new page

- **Nothing saved yet:** unchanged.
- **Already saved itself:** an inline confirmation replaces the footer: "Tear out this page? Its 2 sets go with it." with **Tear out page** (secondary) and **Keep page** (ghost, focused). Keep goes to Sessions with the arrival line "Page kept. It stays open for more sets." Tear out deletes it and goes to Sessions with "Page torn out."

## After the first save

The address changes from `/workouts/new` to `/workouts/{id}/edit` without a new history entry. Back from the editor still goes where it went before.

## Conflict reload

**Reload page** loads the server's version. Sets that existed only in this tab are listed once in the editor's notice area ("Not saved here: Bench Press set 4 · 80 kg × 5"), with **Dismiss**.

## Accessibility

- Status changes are announced politely, at most once per state change (no "Saving… Saved" chatter on every keystroke: "Saving…" is announced only if a save takes over a second).
- The conflict and deleted states are announced assertively, since they need action.
