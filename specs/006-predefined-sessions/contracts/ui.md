# UI Contract: Predefined Sessions

> **Revised (research R15, 2026-10-15).** There is no prepare mode, `/workouts/prepare` route or Prepared group any more. Sessions has **From template** beside **Start a new page**: a chooser of templates, each starting a session at once (SC-007: Open the notebook, From template, the template). The template list has **Start** on each row, and the template editor's secondary action is **Start session**. Everything about the in-progress editor, the finished-page editor, the session page and templates below still holds; `docs/ui/README.md` is the as-built record.

**Status**: brief for the UI mock. Issue #165 requires a mock made with `/impeccable` and approved by the owner before any frontend work. The mock updates `docs/ui/README.md` and `docs/ui/prototype.html` in its own PR. This file lists what the mock must cover and the rules it must keep. Layout, copy and visual treatment are the mock's to propose. All copy below is a placeholder.

## Routes (proposed)

| Route | Screen |
| --- | --- |
| `/workouts/prepare` | New prepared page (the editor in prepare mode) |
| `/workouts/{id}/edit` | Unchanged route. The editor picks its mode from the page state: prepared, in progress or finished. |
| `/templates` | Template list |
| `/templates/new`, `/templates/{id}` | Template editor |

## What each screen must do

**Sessions (`/workouts`)**
- A "Prepared" group above the flat history list, newest first.
  - Each row shows the title (or "Untitled plan"), an exercise summary and a set count.
  - Each row has **Start** and opens the prepared page.
- The history list stays flat and ungrouped by date (rule 1). The prepared group is not dated, so it doesn't break that rule.
- Entry points to prepare a page:
  - blank
  - from a template
  - one to the template list
- SC-007: from the cover, at most three taps to a prepared page made from a template.

**Editor, prepare mode**
- Same blocks, autocomplete, "last time" hints and set rows as today, without done controls.
- A dateline with no date ("Plan · add details").
- An explicit **Save plan**, plus **Start session**, which saves first, then starts with the current date and time.
- **Save as template** is available.
- Cancel and discard behave as on a finished page's editor.

**Editor, in progress** (a started page, prepared or not)
- Every set row gets a **done** toggle.
  - A 44px target (rule 10).
  - `aria-pressed`, named "Done".
  - Visually distinct from the Warm-up toggle.
- Done and not-done rows differ by more than colour.
- The first not-done set in page order is marked as **next**.
- One tap on a planned row's toggle marks it done and saves at once, as a completed row does today.
- Weight and reps stay editable before and after done (SC-003).
- New rows start done (research R14, open for the owner).
- Finish session: when not-done sets remain, the confirmation names them ("2 planned sets will be kept as skipped.") before **Confirm finish**.

**Session page (`/workouts/{id}`)**
- Skipped sets are shown, demoted like warm-ups, with a "skipped" tag. They're excluded from the block's best figure (rule 2's sibling).
- In progress: planned sets are shown as still to do.
- **Save as template** sits beside Edit and Delete.

**Editor, finished page**
- Skipped rows have the done toggle, so a forgotten tap can be fixed (FR-010a).
- Skipped rows can be removed as any row.

**Templates**
- The list is ordered by name.
- The editor has a name field, then exercise blocks and set rows as in the page editor, with the same warm-up and bodyweight rules and no done controls.
- **Save**, **Delete** (inline confirm naming what goes: "Delete template Push A? Sessions made from it stay."), and **Prepare a session**.
- A taken name shows inline at the name field: "You already have a template called Push A." The input is kept.

## Rules the mock must keep

All of `docs/ui/README.md` → "Rules the UI must not break" still holds. In particular:
- 44px targets
- gold never carries text
- errors only in `--color-error`
- `HH.mm` times
- no create-exercise action
- destructive actions name what they destroy

The new states (planned, done, skipped, next) must work in light and dark mode and must not rely on colour alone.
