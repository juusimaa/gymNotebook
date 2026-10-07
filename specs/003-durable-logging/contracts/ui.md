# UI Contract: Durable Logging

Only the session editor changes on screen. All copy is proposed and open to review.

## Save state in the date line

The date line ("Today 09.34 · add details") gains the save state at its end, as a polite live region. It never moves focus.

| State | Text | Style |
| --- | --- | --- |
| Nothing to save yet | (nothing) | |
| Waiting / saving | "Saving…" | muted |
| Saved | "Saved 09.42" | muted |
| Failed, retrying | "Not saved — retrying" | `--color-error` |
| Incomplete row for 60 s | "Set 3 needs reps" (or "weight") | muted |
| Conflict | "This page changed on another device." + **Reload page** (44px text button) | `--color-error` |
| Deleted elsewhere | "This page no longer exists." | `--color-error` |

## Footer

- **In-progress page** (new, or Continue logging): only **Finish session** (primary, full width), which flushes any pending save, then confirms as in #118. No Save button: autosave covers it (owner, 2026-10-07).
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
