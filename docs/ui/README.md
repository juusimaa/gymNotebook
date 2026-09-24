# UI specification — Gym Notebook

Status: proposed. Closes the "UI design — deliberately deferred" open item in [`PLAN.md`](../../PLAN.md); scope is milestones 5, 7, 8 and 9.

- **Interactive prototype:** [`prototype.html`](./prototype.html) — one self-contained file, no build step. Open it in a browser (or via GitHub Pages) and click through: sign in → cover → sessions → a session page → new page, plus progress and the exercise index off the sessions header.
- **Primary device:** phone, 390 × 844. Desktop is the same single column, centred, max-width ~430px. No separate desktop layout in this pass.

## Visual direction

Editorial paper, not skeuomorphic notebook. Serif display type (Cormorant Garamond) over a serif body (Lora) on a warm near-white ground, hairline rules instead of boxes and fills, a single gold accent used as stroke — outlined buttons, rules, the chart's line — never as a filled block. Numbers set tabular everywhere they stand as figures (set rows, times, weights, dates, chart axes).

Tokens live in the prototype's stylesheet as CSS custom properties: `--color-bg #f3f2f2`, `--color-text #201f1d`, `--color-accent #b68235`, `--color-divider`, 100–900 ramps per role, a 4.6px-step spacing scale, 2/4/7px radii and three shadow levels. The frontend lifts these into `tokens.css` and references them by variable — no hard-coded hexes in components.

**Component-library decision (the open item in PLAN.md):** none. The screens need inputs, buttons, tags, lists and one chart; the whole surface is small enough that hand-written CSS against the tokens is less work than restyling a library out of its own look. The chart is hand-drawn SVG (see Progress) and did not change this.

## Screens

### 1. Login — `/login`
Email, password, invite code (labelled optional for existing accounts), one primary action, plus "Create account" and "Change password" links. Backs onto `POST /auth/login` and `POST /auth/register`; the invite code field is only sent on register. A 401 returns here with the email preserved. Rate-limit rejections (429) show an inline message under the button, not a toast.

### 2. Cover — `/`
The page you land on after login; the owner name comes from `GET /auth/me`. Volume, year, one "Open the notebook" action, "Sign out" below it. Deliberately carries no data — it is the closed cover of the book, and its job is to make opening the log a decision rather than a dashboard. Sign-out drops the token client-side.

### 3. Sessions — `/workouts`
`GET /workouts?limit=&before=`, newest first, flat — **no date grouping**. Each row: day + month numeral on the left, title, start time, an exercise-name summary and a meta line (`3 exercises · 9 sets · 07.15–08.40`) — all of it from `exerciseNames`, `exerciseCount`, `setCount` and `endedAt` on the row, so the list is one request. Two sessions on one date are two adjacent rows distinguished only by their times; the prototype's sample data contains such a pair (8 Sep) and it must survive any list refactor. Header links to Progress, Exercises and the cover; sticky primary action at the bottom: "Start a new page".

### 4. Session page — `/workouts/{id}`
`GET /workouts/{id}`. Heading block: long date kicker, title, then start–end, bodyweight and gym on one meta line; absent optional fields degrade to plain text ("bodyweight not logged"), never to an empty slot. Then one block per `WorkoutExercise` in `position` order, each with its computed best on the right — `e1RM 99 kg`, or `best 8 reps` when the block's `isBodyweight` is set. Set rows are `n / load / tag`; warm-up sets are set in `--color-neutral-600` with a "warm-up" tag and are excluded from the best figure. Notes justified at the bottom.

Edit and Delete sit in the header. Delete opens an inline confirm in place ("Tear out this page? Its exercises and sets go with it.") rather than a modal — it names the cascade, since that is what `DELETE /workouts/{id}` does.

### 5. New page — `/workouts/new`
Heading fields first (date, started, title, bodyweight, gym), then exercise blocks. Each block: name, a kg/bodyweight marker, and set rows of `weight / reps / warm-up toggle`; a bodyweight block hides the weight field unless added weight is enabled. "Add set" appends a row; `setNumber` is never sent — the server assigns it. Below the blocks, an autocomplete field backed by `GET /exercises?search=` whose suggestions show `lastSet`; picking one appends a block. When no exact name matches, the user adds the typed name as either Loaded (kg) or Bodyweight; the bulk save creates it and a follow-up `PATCH /exercises/{id}` persists a new bodyweight choice. The whole exercise list commits with one `PUT /workouts/{id}/exercises`. "Save page" leaves the session in progress; "Finish session" first opens an inline confirmation with a distinct "Confirm finish" button, and confirming writes `ended_at`.

### 6. Progress — `/progress`
`GET /exercises/{id}/history?from=&to=`. A horizontally scrolling exercise picker (selected one carries the accent stroke), then the exercise name and a metric line that states what is plotted and in what unit — **"Best e1RM per session · kg"**, or **"Best reps per session · reps"** for a bodyweight exercise. The y-axis label changes with it; nothing else about the screen does.

The chart is hand-drawn SVG: three hairline gridlines, one accent polyline, hollow points, and HTML axis labels positioned over it (labels are HTML, not `<text>`, deliberately — see Implementation notes). The y-range is the data's own range plus ~12%, snapped to 2.5 kg, so a plateau reads as a plateau instead of being flattened by a zero baseline.

Under the chart, Latest / Best / Change figures, then one row per session — date, the set it came from, the figure, and the change from the session before, with gains in `--color-accent-700`. Newest first, matching the sessions list.

Three things the screen must say out loud, because they are the plan's e1RM rules made visible:

- A tested single charts as the weight itself, and its row is annotated as a tested single — never an Epley-inflated estimate.
- A bodyweight exercise charts reps, and a line under the chart says so and that belt-loaded sets are not charted here.
- For loaded exercises that line instead warns that estimates degrade above roughly 12 reps.

Two sessions on one date are two points, not one — the Pull-up series in the prototype has such a pair (8 Sep morning and evening).

### 7. Exercise index — `/exercises`
`GET /exercises?search=`: a search field, then every exercise with its session count, `lastSet` and a kg/bodyweight marker. One line of prose explains that exercises come into being by being used — there is no "add exercise" action anywhere, and the absence needs accounting for. The prototype's list deliberately contains a typo'd "Bnech Press" so the merge case is demonstrable.

### 8. Edit exercise — `/exercises/{id}`
`PATCH /exercises/{id}`, and the escape hatch for names typed wrong. Shows the name as logged with its history count, then:

- **Name** — a text field, with a note that casing and spacing are the user's to keep ("Back Squat" and "back squat" are the same exercise underneath), which is `normalized_name` explained without naming it.
- **Merge** — when the typed name normalizes onto another existing exercise, an accent-stroked notice appears and the primary action becomes "Merge into <name>". It states how many sessions move, that the old name disappears from the index, that nothing is deleted, and that it can't be undone from here. The merge is not a separate screen or a second confirm: it is the same rename, and the server treats it as one.
- **Load** — a switch between "Loaded (kg)" and "Bodyweight", each with its consequence spelled out (reps vs e1RM; weight means *added* weight). This is the read/write surface for `is_bodyweight`.
- **Remove** — explained, not offered. An exercise with sets against it can't be deleted; the copy says so and points at renaming-onto as the fix.

### Privacy and account lifecycle — design preview

These prototype screens explore `specs/001-privacy-account-lifecycle/spec.md`. They are clickable design samples, not a published privacy notice or working account operations. Controller details, legal bases, processor/transfer information, contact details and provider retention settings remain review gates in the feature specification.

- **Public notice:** A "Privacy notice" link appears on the sign-in screen. The preview shows the content hierarchy, including what users enter, why it is used, retention and rights. Its own header says the legal text is pending review. Returning to sign-in does not record an acknowledgement.
- **New notice version:** "Open the notebook" on the cover first shows the notice when the current version has not been acknowledged. "Continue to notebook" records the version shown in this prototype's in-memory state; it is explicitly not consent. Leaving without continuing causes the notice to appear again on the next attempt. The account privacy screen has a prototype control for previewing a revised version. Sign-out and sign-in in the same open prototype retain the acknowledgement for review; reloading the page resets all demo state.
- **Account privacy:** "Privacy & account" sits on the cover, separate from workout navigation. It links to the notice, export and deletion. The prototype screens use the existing single-column layout and design tokens.
- **Export:** The user sees the contents and privacy warning, enters a current password and downloads one *sample* JSON file with a field guide. An empty password or the literal `wrong` shows an inline verification error; a prototype link shows a recoverable export failure. No real account data is read.
- **Deletion:** The review screen names the active information removed, sign-out across sessions, optional export, irreversibility, the proposed 30-day backup limit and the restricted security-log exception. It requires a separate password-confirmed action and offers cancellation. An empty password or `wrong` shows an inline error; a prototype link shows a recoverable failure. Success leads to a completion screen explaining backup and log expiry. No real account is deleted.

All action buttons are at least 44px high and errors are shown beside the relevant action. The final notice wording and service behavior must be reviewed against the specification before implementation; the prototype is for layout and flow review.

## Rules the UI must not break

These are UI-visible consequences of decisions in PLAN.md; each one is a thing a redesign can quietly undo.

1. A page is a session, not a day — never group or merge rows by date, in the list or on the chart.
2. Warm-up sets are visually demoted and excluded from the progress figure.
3. Bodyweight exercises show reps, not kg; added weight shows as `+10 kg × 5`.
4. `reps == 1` shows the weight itself, not an Epley estimate, and says that it is a tested single.
5. The chart always states its metric and unit; the y-axis label follows the exercise's `isBodyweight`.
6. kg only, one decimal for bodyweight, 1.25 kg steps for plates.
7. Optional heading fields are genuinely optional and read as prose when absent.
8. Destructive actions name what they destroy: a page delete says the sets go with it, a merge says how many sessions move.
9. There is no "create exercise" action — names arrive by being typed into autocomplete.
10. Hit targets ≥ 44px — this is used standing at a rack, one-handed.
11. Times use Finland's 24-hour `HH.mm` convention (`07.15`), even though the interface language is English.

## Implementation notes

- **Chart axis labels are HTML, not SVG `<text>`.** They sit absolutely positioned over the chart box (y ticks) and in a flex row beneath it (first/last date). This keeps the labels in the same type system as the rest of the page and out of SVG's text metrics; the drawn SVG is only gridlines, the polyline and the points.
- The chart's plot area leaves a 38px left gutter for the y labels; both the SVG and the label column are positioned against it.
- `is_bodyweight` is read from the block (`GET /workouts/{id}`) and from the autocomplete result (`GET /exercises`), and written only here.

## Still open

- Empty states (no sessions yet, no exercises to search, an exercise with one session and so no chart to draw) and error/offline states are not drawn.
- Date range filtering on the progress view: the endpoint takes `from`/`to`, the screen currently plots everything.

## How this lives in the repo

```
docs/
└── ui/
    ├── README.md       # this spec
    └── prototype.html  # self-contained clickable prototype
```

Committed on a branch via PR like any other change. The prototype is a reference artifact, not a build input — nothing under `frontend/` imports it. When a screen's design changes, update the prototype and this file in the same PR that changes the code, so the spec can't drift silently. Serving `docs/` with GitHub Pages gives the prototype a shareable URL; linking it from `README.md` and from PLAN.md's frontend section is what makes it discoverable.
