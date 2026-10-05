# Design fix plan — from the 2026-10-04 critique

Status: steps 1–6 done. Work through the steps in order; each step is its
own branch and PR (AGENTS.md: small, reviewable PRs), and each PR updates this
spec ([README.md](./README.md)) and the [prototype](./prototype.html) when a
screen changes. Tick a step off here in the PR that ships it.

## Where this comes from

Two Impeccable critiques ran on 2026-10-04:

| Run | Target | Score | Method |
|---|---|---|---|
| Source review | `frontend/src` + prototype | 26/40 | code and prototype read as code; no browser |
| Live review | `http://localhost:5173` (test account) | **22/40** | real app in Chrome, phone width measured in a 390 × 844 frame, detector overlay on 6 screens |

The snapshots live in `.impeccable/critique/`, which is machine-local and
gitignored, so the findings this plan needs are restated below. Re-run
`/impeccable critique http://localhost:5173` after the last step to compare
with 22/40.

**What to keep:** the notebook voice in the copy, honest block metrics
("e1RM 58 kg", "warm-ups only"), the Progress caveat, the deletion copy, the
44px inputs with `inputmode` and per-set aria-labels. The read-back screens
are the strongest part of the product; the logging screen is the weakest.

## Decisions already made

- **Start with the logging flow** — it is the product's core promise
  ("Fast at the rack", PRODUCT.md principle 3).
- **Gold becomes decoration only.** `--color-accent` (`#b68235`, 3.02:1 on
  `--color-bg`) stays for rules, outlines and the cover ornament; interactive
  text moves to `--color-accent-700` (`#7d5411`, 5.97:1).
- **Scope: all five priority issues**, split into the PRs below.

## Open decisions (settle in the step that needs them)

- ~~**Main-action emphasis vs. "never a filled block".**~~ Settled in
  step 4: no fill. The one primary per screen is a heavier outline.
- ~~**Save page vs. Finish session emphasis.**~~ Settled in step 2: Finish
  session is primary on a new page.

## Steps

### 1. Draft safety — `/impeccable harden` *(issue 2, P1)*

Problem: "← Cancel" in the editor discards everything with no confirm, and
the draft lives only in React state — a phone lock, tab eviction or reload
during a rest loses the session. Breaks principle 4, "Nothing is lost by
accident".

- [x] Persist the editor draft to browser storage, keyed per route (new page
      vs. `workouts/{id}/edit`), and restore it with a short notice.
- [x] Clear it on successful save, on sign-out and on session invalidation
      (`auth/invalidation.ts`), as the spec already requires for drafts.
- [x] Cancel asks "Discard N sets?" inline (no browser `confirm`) when the
      draft has changed; leaves silently when it has not.
- [x] Vitest coverage for the draft serialise/restore helper.

Shipped decisions (owner, 2026-10-04): `sessionStorage`, not `localStorage`;
a plain token expiry *holds* the draft for the same user instead of clearing
it, because the 30-minute token would otherwise discard every long session's
draft at Save; the four optional details are stored only with consent and
stripped on withdrawal; P5 in `docs/privacy/processing-decision.md` was
redrafted for owner signature. The helper lives in its own module,
`screens/editorDraftStorage.ts`, next to `newWorkoutDraft.ts`.

Goes first because the later editor changes build on a draft that survives.

### 2. Editor structure — `/impeccable layout` *(issue 1 part 1, P1)*

Problem: on 390 × 844 the "Add exercise" input starts at y = 834 — below
the fold and under the sticky Save/Finish footer (top at y = 771) — because
Date, Started, Title, Bodyweight, Gym and Notes come first. Exercise blocks
are filled cards (`NewWorkout.css:68-73`) where the spec says hairlines.

- [x] Put exercises first; collapse the heading into one line
      ("Today 09.34 · add details") that expands on tap.
- [x] Replace block card fills with hairline rules.
- [x] One column header for WEIGHT / REPS instead of 10px labels on every row.
- [x] Keep the suggestion list and "Add exercise" clear of the sticky footer.
- [x] Settle the Save/Finish emphasis (open decision above).

Shipped decisions (owner, 2026-10-04): **Finish session** is the primary
action on a new page and **Save changes** on the edit page; the prototype
(which had Save page primary) was corrected to match. The folded heading
reads "Today 09.34 · add details", opens in place, and reopens by itself
when a save fails on one of its fields. With exercises first, "Add exercise"
moves from y = 834 to y = 186 on an empty page at 390 × 844.

### 3. Set entry speed — `/impeccable optimize` *(issue 1 part 2, P1)*

Problem: "+ Add set" adds an empty row (`addEmptySetToExercise` in
`newWorkoutDraft.ts`) and focus stays on the button, so weight and reps are
retyped for every set — paper's ditto mark (〃) costs about eight keystrokes.
The last set is shown only inside autocomplete, not in the block.

- [x] "+ Add set" copies the previous set's weight and reps and focuses the
      new weight field (values selected for quick overwrite).
- [x] Show "last time: 50 × 5" under each block heading (`lastSet` already
      comes from `GET /exercises`).
- [x] Remove-set ×: 44px wide (it is 30px, `NewWorkout.css:179`), further
      from the warm-up toggle, with an inline "Removed … · Undo" line instead
      of instant loss. Same Undo for removing a whole block.
- [x] Warm-up toggle keeps a stable accessible name ("Warm-up", with
      `aria-pressed`) instead of flipping between "Working" and "Warm-up".
- [x] Validation marks the offending field (`aria-invalid` +
      `aria-describedby`) rather than only a footer message.

Shipped decisions (owner, 2026-10-04): the ditto copies the warm-up flag
too, so a run of warm-ups stays warm-ups; the Undo line has no timer and
lasts until the next edit in that block (a removed block's, until the next
removal or added exercise), one removal at a time. "last time" appears only
on a new page's blocks picked from autocomplete — on the edit page the
server's latest set may be the page's own. Measured at 390 × 844: a repeated
set went from about 8 actions (tap Add set, tap weight, type 3 digits, tap
reps, type 1) to 1, and the × is 44 × 44 with 10px to the toggle (was 30px
wide, 6px away).

### 4. Search order and main actions — `/impeccable clarify` *(issues 4 and 5, P2)*

Problems: typing "Taka" lists "Add Taka as Loaded / Bodyweight" above the
existing "Takakyykky", inviting typo exercises. On every screen the main
action is the weakest element: "Open the notebook" is styled like "Sign out",
"Start a new page" is a ghost button, and an in-progress session is
reachable only through a 25 × 20 "Edit" link.

- [x] Autocomplete: existing matches first, the create option last.
- [x] One clearly primary action per screen (see open decision on fills).
- [x] "Continue logging" on an in-progress session page, and on in-progress
      rows in the Sessions list (which also print the start time twice).

Shipped decisions (owner, 2026-10-04): the primary stays a stroke, now 2px
`--color-accent-700` with a 17px label and a 48px target (1.5px was tried and
renders as 1px on 1× screens); the cover's "Open the notebook" gets its
stroke back. The add-as choice says which case it is: "Not in the list? Add
Taka as a new exercise:" under matches, "No exercise called Taka yet. Add it
as:" when there are none. An in-progress session page shows **Continue
logging** as its primary at the bottom and hides the header's Edit (one way
into the editor, not two). An in-progress Sessions row reads "in progress" in
place of its start time, ends its meta line "from 19.34", and carries a
secondary **Continue logging** beside the row link; "Start a new page" stays
the list's one primary. Not changed: a finished row still shows its start
both beside the title and in "07.15–08.40". That is the spec'd layout, and it
is what tells same-date sessions apart.

### 5. Contrast and hit targets — `/impeccable audit` → `/impeccable polish` *(issue 3, P1)*

Problems measured on the live app: the detector found 23 low-contrast and 15
undersized-text instances across 6 screens.

- [x] Interactive text → `--color-accent-700` (decision above).
- [x] Muted ink `--color-neutral-600` (`#7d7979`, 3.8:1) → `--color-neutral-700`
      for kickers, meta lines, stat labels — clears 21 of the 23 contrast hits.
- [x] Placeholders off `--color-neutral-500` (2.6:1).
- [x] No text under 11px (all 15 hits are 10px: session-row month, "warm-up"
      tag, Notes heading, Latest/Best/Change, kg/bodyweight, chart ticks).
- [x] Header nav and back links get `min-height: var(--hit-target)` — they
      measure 20px tall today (`Sessions.css:30-36`, and the matching rules in
      `WorkoutDetail.css`, `Progress.css`, `NewWorkout.css`); "Edit" is 25 × 20
      next to a 39 × 44 "Delete".
- [x] Progress caveat: drop `text-align: justify` (`Progress.css:245`).

Shipped decisions (owner, 2026-10-04): focus rings, the input's focus border
and the caret move to `--color-accent-700` with the text, because plain gold
measures 2.78:1 on `--color-surface` (the "Tear out this page?" band), under
the 3:1 a focus indicator needs. Gold stays for hairline outlines, the chart
line, the selected chip's border and the cover ornament. Every text use of
`--color-neutral-600` and `-500` became `-700`; warm-up rows still recede
(5.83:1 against full ink, plus their tag). Header text links share one
`.header-link` class in `base.css` that gives them a 44 × 44 minimum box;
"Delete" uses it too. Not changed: session notes stay justified, as the spec
says. Measured at 390 × 844 on 14 routes (cover, sessions, both session page
states, new and edit page, progress, exercise index and edit, the four
privacy/account screens, change password): 0 text under 4.5:1 (was 23), 0 text
under 11px (was 15), no header link under 44px, no horizontal overflow.

### 6. Final pass — `/impeccable polish`

- [x] Progress chip row clips at 390px ("Ylätalja vastaotteella" ends at
      x = 486) — add a scroll affordance.
- [x] Stat figures: Cormorant tabular "11" reads as Roman "II"; use Lora or
      old-style figures for numerals.
- [x] Chart: round y-ticks (not 57.5 / 58.8 / 60); no duplicate "4.10." x-ticks
      when sessions share a date.
- [x] Session page: drop the "WORKING" tag on normal sets; only warm-ups need one.
- [x] Progress header: the `aria-disabled` "Exercises" span looks like a link
      (`Progress.tsx:139`) — link it or remove it.
- [x] Privacy screens: add an `h1`; show the notice version and date in the UI
      language (it shows "dev-2026-09-25" and "4. lokakuuta 2026").
- [x] Edit exercise: disable "Save name" until the name changes.
- [x] "Last" hint: shows a warm-up when the latest session for that exercise
      had only warm-ups — decide whether that is honest enough.
- [x] Editing an in-progress session shows two primaries: **Save changes**
      and **Finish session** are both `btn-primary` (`NewWorkout.tsx:1437`,
      `:1449`). Found in the step 5 browser pass; step 2 says Save changes is
      the edit page's primary.
- [x] The cover's "Buy me a coffee ↗" link is 15px tall — the one target
      under 44px the step 5 pass found outside the headers.
- [x] Re-run `/impeccable critique http://localhost:5173` and record the score here.

Shipped decisions (owner, 2026-10-05): the "last time" hint stays honest by
saying so — `lastSet` gains `isWarmup` and the hint, the suggestions and the
exercise index print "20 kg × 10 · warm-up"; the server's choice of set is
unchanged. An edit page whose session is still in progress keeps **Finish
session** as its one primary (it is reached by Continue logging, so it is a
logging screen); a finished session's edit page keeps **Save changes**. Lora
replaces Cormorant for every figure, not only the Progress stats: the
Sessions day number and the session page's best line too. Y-ticks use a 1/2/5
step aiming at three intervals, so a chart shows three to five ticks. The
privacy screens' h1 keeps the old 32px size, and Change password gets the
same h1. Not changed: the notice's version identifier ("dev-2026-09-25" is
the dev config's value, shown as the server sends it). Edit exercise's Save
reads "Save changes" once the load has changed, since it saves more than the
name.

Re-run, 2026-10-05, on `http://localhost:3000` (the Compose build of this
branch; Chrome would not size below 500px, so the 430px column was measured
in a 500 × 763 window): **27/40** (was 22/40). Detector: 0 CLI findings in
`frontend/src` and the prototype; the overlay flags only the kicker above
each h1, which is the spec'd notebook look. Measured on 8 routes: 0 text
under 4.5:1, 0 text under 11px, 0 targets under 44px, no horizontal
overflow. What the review still finds, for a next round: error text uses the
link colour and the login error sits far below Sign in; the first set of a
picked exercise starts blank with no focus and no Enter-to-advance; nothing
warns about a second page while one is in progress; Progress opens on the
alphabetically first exercise and mixes "4.10." with "04 Oct" and whole
figures with one-decimal changes; "Untitled session" fills the title slot
without consent; header link order differs per screen.
