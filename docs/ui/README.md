# UI specification — Gym Notebook

Status: proposed. Closes the "UI design — deliberately deferred" open item in [`PLAN.md`](../../PLAN.md); scope is milestones 5, 7 and 9.

- **Interactive prototype:** [`prototype.html`](./prototype.html) — one self-contained file, no build step. Open it in a browser (or via GitHub Pages) and click through: sign in → cover → sessions → a session page → new page.
- **Primary device:** phone, 390 × 844. Desktop is the same single column, centred, max-width ~430px. No separate desktop layout in this pass.

## Visual direction

Editorial paper, not skeuomorphic notebook. Serif display type (Cormorant Garamond) over a serif body (Lora) on a warm near-white ground, hairline rules instead of boxes and fills, a single gold accent used as stroke — outlined buttons, rules, small marks — never as a filled block. Numbers set tabular everywhere they stand as figures (set rows, times, weights, dates).

Tokens live in the prototype's stylesheet as CSS custom properties: `--color-bg #f3f2f2`, `--color-text #201f1d`, `--color-accent #b68235`, `--color-divider`, 100–900 ramps per role, a 4.6px-step spacing scale, 2/4/7px radii and three shadow levels. The frontend should lift these into one `tokens.css` (or the equivalent in whatever styling approach milestone 5 picks) and reference them by variable — no hard-coded hexes in components.

**Component-library decision (the open item in PLAN.md):** none. The screens need inputs, buttons, tags, a list and a modal-free flow; the whole surface is small enough that hand-written CSS against the tokens is less work than restyling a library out of its own look. Revisit if a date picker or chart library drags one in — the chart in milestone 8 is the likeliest reason.

## Screens

### 1. Login — `/login`
Email, password, invite code (labelled optional for existing accounts), one primary action, plus "Create account" and "Change password" links. Backs onto `POST /auth/login` and `POST /auth/register`; the invite code field is only sent on register. A 401 returns here with the email preserved. Rate-limit rejections (429) show an inline message under the button, not a toast.

### 2. Cover — `/`
The page you land on after login. Owner name, volume, year, one "Open the notebook" action, "Sign out" below it. Deliberately carries no data — it is the closed cover of the book, and its job is to make opening the log a decision rather than a dashboard. Sign-out drops the token client-side (per PLAN.md).

### 3. Sessions — `/workouts`
`GET /workouts?limit=&before=`, newest first, flat — **no date grouping**. Each row: day + month numeral on the left, title, start time, an exercise-name summary and a meta line (`3 exercises · 9 sets · 07:15–08:40`). Two sessions on one date are two adjacent rows distinguished only by their times; the prototype's sample data contains such a pair (8 Sep) and it must survive any list refactor. Sticky primary action at the bottom: "Start a new page".

### 4. Session page — `/workouts/{id}`
`GET /workouts/{id}`. Heading block: long date kicker, title, then start–end, bodyweight and gym on one meta line; absent optional fields degrade to plain text ("bodyweight not logged"), never to an empty slot. Then one block per `WorkoutExercise` in `position` order, each with its computed best on the right — `e1RM 99 kg`, or `best 8 reps` for `is_bodyweight` exercises. Set rows are `n / load / tag`; warm-up sets are set in `--color-neutral-600` with a "warm-up" tag and are excluded from the best figure. Notes justified at the bottom.

### 5. New page — `/workouts/new`
Heading fields first (date, started, title, bodyweight, gym), then exercise blocks. Each block: name, a kg/bodyweight marker, and set rows of `weight / reps / warm-up toggle`; a bodyweight block hides the weight field unless added weight is entered. "Add set" appends a row; `setNumber` is never sent — the server assigns it. Below the blocks, an autocomplete field backed by `GET /exercises?search=` whose suggestions show the last logged load; picking one appends a block. The whole page commits with one `PUT /workouts/{id}/exercises`. "Finish session" writes `ended_at`.

## Rules the UI must not break

These are UI-visible consequences of decisions in PLAN.md; each one is a thing a redesign can quietly undo.

1. A page is a session, not a day — never group or merge rows by date.
2. Warm-up sets are visually demoted and excluded from the progress figure.
3. Bodyweight exercises show reps, not kg; added weight shows as `+10 kg × 5`.
4. `reps == 1` shows the weight itself, not an Epley estimate.
5. kg only, one decimal for bodyweight, 1.25 kg steps for plates.
6. Optional heading fields are genuinely optional and read as prose when absent.
7. Hit targets ≥ 44px — this is used standing at a rack, one-handed.

## Still open

- Where `is_bodyweight` gets set: the prototype infers it from the exercise and shows the marker read-only. Ask-once-on-create is the option that fits these screens best.
- Progress view (milestone 8) and the exercise rename/merge screen (milestone 9) are not designed yet.
- Empty states (no sessions yet, no exercises for autocomplete) and error/offline states are not drawn.

## How this lives in the repo

```
docs/
└── ui/
    ├── README.md       # this spec
    └── prototype.html  # self-contained clickable prototype
```

Committed on a branch via PR like any other change. The prototype is a reference artifact, not a build input — nothing under `frontend/` imports it. When a screen's design changes, update the prototype and this file in the same PR that changes the code, so the spec can't drift silently. Serving `docs/` with GitHub Pages gives the prototype a shareable URL; linking it from `README.md` and from PLAN.md's frontend section is what makes it discoverable.
