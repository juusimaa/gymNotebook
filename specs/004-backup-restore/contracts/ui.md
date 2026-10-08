# UI Contract: Backup and Restore

One new screen, `/backup`, which replaces `/account/export`, and one new link on the cover. All copy is proposed and open to review. The clickable [UI draft](../ui-draft.html) shows every state below; open it in a browser from the repository (it loads the app's fonts from `frontend/public/fonts/`).

## Visual direction

Unchanged: the privacy screens' reading layout, the existing tokens and classes (`.page`, `.privacy-heading`, `.privacy-block`, `.btn-*`, `.field`, `.form-message`, `.muted`). The screen reads as a page at the back of the notebook. Sections are separated by hairline rules, not cards. The one new pattern is the **format choice**: two full-width radio rows, each with a one-line consequence. It's drawn with existing tokens (divider border, accent-700 for the selected ring) and `--hit-target` height.

The draft drops the small-caps kicker above the heading that the privacy screens carry. The heading names the screen on its own. That is a proposal for this screen only; the other privacy screens are untouched.

## Cover

A **Backup & restore** ghost link on its own line below Change password · Sign out, and above Privacy & account when that shows. It is always shown (not tied to the privacy flag). The last-backup date doesn't appear on the cover (owner, plan Q5).

## Backup & restore, `/backup`

Top to bottom:

1. **← Cover** (ghost, 44px), then the heading **Backup & restore** (focus moves here on load).
2. **Last backup line**, below the heading in body size: "Last full backup: **3 October 2026, 09.42**" or "No full backup yet." Muted, with the date in ink and tabular numerals. While loading: "Checking your last backup…". If that request fails, the line is hidden, never shown as an error, since it isn't the screen's job.
3. **Make a backup** (`h2`, ruled block)
   - Format, a `fieldset` with a visible legend "Format":
     - **Full backup (JSON)**, selected by default. "Everything in your notebook. This is the file you can restore from."
     - **Spreadsheet (CSV)**. "One row per set, for Excel or Numbers. It can't be restored."
   - **Current password** field.
   - "The file contains personal information. Keep it somewhere private." (muted)
   - Primary button: **Download backup** or **Download spreadsheet**, following the choice.
4. **Restore from a backup** (`h2`, ruled block)
   - "Adds the pages from a backup that aren't in your notebook. Nothing already here is changed or removed."
   - **Choose backup file** (secondary, full width): a real `<input type="file" accept=".json,application/json">` with the button as its label.
5. **Privacy & account** link at the foot, when the privacy feature is on.

### Backup states

As spec 001's Take a copy (`docs/ui/README.md` → Export), with the new names:

| State | Shown |
| --- | --- |
| Checking | "Checking your password…" |
| Receiving | "Preparing your backup…" (or "…your spreadsheet…") with an indeterminate bar and **Cancel** |
| Complete, JSON | "Saved as gym-notebook-2026-10-08.json. Your notebook has not changed." The last-backup line updates to the new time, with a brief gold underline, the screen's one moment of motion (none under `prefers-reduced-motion`). **Download again**. |
| Complete, CSV | "Saved as gym-notebook-2026-10-08.csv. A spreadsheet can't be restored; keep a full backup too." Last-backup line unchanged. |
| Errors | As 001: wrong password, too many attempts, export already running, busy, didn't finish. The form stays and focus returns to the password field. |

### Restore states

The section changes in place; it never opens a modal.

| State | Shown |
| --- | --- |
| Reading | "Reading gym-notebook-2026-09-01.json…" (checked on the device; nothing sent) |
| Summary | A ruled list, not a card: file name; "Backup taken 1 September 2026, 18.04"; "**212 pages**, 3 January 2025 – 31 August 2026"; "**1,846 sets** · **19 exercises**". Then "Pages already in your notebook are skipped." Buttons: **Restore missing pages** (primary within this confirmation) and **Choose another file** (ghost). Focus moves to the summary. |
| Restoring | "Restoring… Keep this page open." with an indeterminate bar. No cancel: once sent, it completes or rolls back. |
| Done | "Restored **2 pages** and **48 sets**." then, only when they apply: "212 pages were already in your notebook."; "New exercise: Sääriprässi."; "Pull-up kept its current type (Bodyweight)."; "Titles, gyms, notes and bodyweight weren't restored, because optional workout details aren't allowed." with **Optional workout details**. Then **Open sessions** (secondary) and **Restore another file** (ghost). |
| Nothing to restore | "Nothing to restore. All 212 pages are already in your notebook." |
| Not a backup | "This isn't a Gym Notebook backup. Choose the .json file from Make a backup. Spreadsheets can't be restored." |
| Newer version | "This backup was made by a newer version of Gym Notebook. Reload the app and try again." |
| Damaged | "This file is damaged or incomplete, so nothing was restored. Try another backup." |
| Too large | "This file is larger than 25 MB, so it can't be restored here." |
| Already running | "A restore is already running for your notebook. Wait for it to finish." |
| Failed | "The restore didn't finish, so nothing was added. Your notebook has not changed. Try again." with **Try again** |

Every failure keeps the chosen file's name visible with **Choose another file**. Failures are `role="alert"` and in `--color-error`; progress and results are a polite live region. A 401 signs out as everywhere else.

## Accessibility

- The format choice is a `fieldset`/`legend` with native radios. Each row is a `label`, so the whole 44px+ row is the target. Arrow keys move between the two options.
- The file control is the native input, visually hidden but focusable, with a visible focus ring on its label-button.
- Focus moves to the restore summary when it appears and to the result line when restore ends, so screen-reader and keyboard users land on what changed.
- Numbers that line up in a column (the restore summary) use `.num` (tabular); numbers in sentences keep proportional figures. Dates and times follow the app's `HH.mm` display convention. The CSV uses `HH:mm` for spreadsheets (API contract).
