# Gym Notebook — Project Plan

A learning project to build a digital replacement for a paper gym log/notebook: track workouts (exercises, sets, reps, weight) and visualize strength progress over time.

## Goals

- Full-stack educational project — type as much code as possible by hand.
- Backend in **C#** (new for this author — previous project used Python/FastAPI).
- Mirror the successful patterns from the `docker-subscription-tracker` project: one container image each for backend and frontend, Docker Compose for local dev, PR-only workflow, eventual Azure deployment via Neon Postgres.

## Stack

- **Backend:** ASP.NET Core Web API (Minimal APIs, not MVC controllers) + Entity Framework Core + Npgsql
- **Frontend:** React + TypeScript (Vite)
- **Database:** PostgreSQL (Neon for the eventual Azure deploy, same as subscription-tracker)
- **Containers:** a Dockerfile each for backend and frontend, tied together by Docker Compose for local dev — the subscription-tracker layout. Two containers rather than one combined image, for the more realistic multi-container practice.
- **Testing:** xUnit + Testcontainers on the backend, Vitest for shared frontend helpers
- **CI:** GitHub Actions — tests as a required check on `main`, images published to GHCR
- **Hosting:** Azure (later milestone, once the app works locally)
- **Workflow:** every change via a branch + PR, no direct commits to `main`

## Domain background: what a gym notebook actually contains

A paper training log is organized by session, then by exercise, then by set:

```
Date: 2026-09-10        Leg day
Started 07:15, finished 08:40
Bodyweight 78.4 kg      Gym: Liikuntamylly

Back Squat
  Set 1: 60kg x 5   (warm-up)
  Set 2: 80kg x 5
  Set 3: 90kg x 3    <- working set
  Set 4: 90kg x 3    <- working set

Bench Press
  Set 1: 40kg x 8
  Set 2: 60kg x 5
```

Weight commonly varies *per set* (warm-up sets are lighter than working sets), so the data model tracks weight and reps at the set level, not one weight per exercise entry.

Two structural facts follow from this being a *notebook*, and both drive the model:

- **A page is one session, not one day.** Train twice on a Tuesday and you tear off two pages, both dated Tuesday. Nothing about a paper log makes a date unique.
- **The heading carries more than the date.** Start time, what the session was called, bodyweight that morning, which gym — these are written at the top before the first exercise, and they're the context that makes the numbers underneath interpretable months later.

## Data model

```
User            (id, username, password_hash, token_version, created_at)
Exercise        (id, user_id, name, normalized_name, is_bodyweight, created_at)
Workout         (id, user_id, date, started_at, ended_at?, title?, bodyweight_kg?, location?, notes?, created_at)
WorkoutExercise (id, workout_id, exercise_id, position)
SetEntry        (id, workout_exercise_id, set_number, weight?, reps, is_warmup)
```

`WorkoutExercise` is the exercise name written above a group of sets, partway down the page — not to be confused with the page's own heading, which is the `Workout` row's date/time/title fields. One row per *appearance* of an exercise in a session, ordered by `position`, with that block's sets hanging off it. It exists because the paper log is session → exercise → set, and hanging sets straight off the workout loses the middle level: nothing would record that Back Squat came before Bench Press, and coming back to squats for a second block later in the session would be indistinguishable from mis-ordered sets. It also makes `set_number` unambiguous — it counts within one block, not within the whole workout.

- `User.token_version` is an integer embedded in every JWT as a `tv` claim and compared on each request. Bumping it invalidates every token already issued for that user — which is what makes "change my password" actually log out the sessions the old password could reach. Without it a stolen token stays valid until it expires no matter what the user does, and there is no revocation story at all. It's a column, so it costs nothing now and a migration later.
- `Exercise` is per-user free text (e.g. "Back Squat"), captured once and offered back via autocomplete on later entries — no fixed/global exercise list.
- `Exercise.name` keeps the casing/whitespace the user typed (for display); `normalized_name` (trimmed, lowercased, internal whitespace collapsed) is derived in the API layer before save and is unique per `(user_id, normalized_name)`. This dedupes "Back Squat" / "back squat" / " Back squat " into one row per user, without forcing display text to lowercase. Uniqueness is scoped per user, not global — different users can each have their own "Back Squat".
- `is_bodyweight` marks exercises where the load is the lifter (pull-ups, dips, push-ups). Their sets leave `weight` null unless a plate is actually hung from a belt, in which case `weight` holds *added* weight only. This flag is what the progress metric branches on (see below).
- `weight` is `numeric(6, 2)` kilograms — nullable, per the point above. No unit column: kg is what the author trains in, and plate math runs in 1.25 kg steps. If lb support is ever wanted, it arrives as a display-layer preference on `User` over a canonical kg column, which is a cheap migration precisely because every existing row is already kg.
- `is_warmup` flags sets that shouldn't count toward the progress metric (see below).
- **A `Workout` row is one page, so `date` is deliberately not unique per user.** Two sessions on the same Tuesday are two rows, ordered by `started_at`. The progress chart's "per session" therefore means "per workout row", and a hard morning session isn't averaged into an easy evening one.
- **`date` is kept as its own column even though `started_at` contains it**, which looks redundant and isn't. `started_at` is a `timestamptz` — an instant — and the page's date is a *local calendar date*, the one the lifter would write at the top. Those come apart at the edges of the day: a session started 00:30 in Helsinki is 21:30 the previous day in UTC, so deriving the date from the instant would file late-night training on the wrong page and quietly shift points on the progress chart. Storing the date the user means, alongside the instant the session began, keeps both honest.
- **`ended_at` is nullable and duration is derived, not stored.** You will regularly forget to close a session out; a required field that can't be filled is worse than an absent one, and a stored duration would be a second source of truth to keep in sync with the two timestamps that already imply it.
- **`title`, `location` and `bodyweight_kg` are all optional page-heading fields.** `title` is the session label ("Push A", "Week 3 Day 2") and is what makes a history list scannable — a column of bare dates is not. `bodyweight_kg` is `numeric(5, 2)`, same kg-only reasoning as set weight, and earns its place beyond habit: bodyweight exercises chart by reps, and a logged bodyweight is what would later let an unloaded pull-up and a +10kg one be compared honestly. `location` is free text for now rather than its own table — if it ever wants autocomplete, `Exercise` is the pattern to copy.
- Deletes cascade **workout → blocks → sets**: a workout is the unit the user thinks in, and its sets are meaningless without it. Deleting an `Exercise` that still has sets is **refused** rather than cascaded — a stray autocomplete entry is a rename, not a reason to silently destroy training history. Renaming onto an existing name merges the two instead (see the API below).

## Progress metric: estimated one-rep max (e1RM)

Raw weight isn't comparable across different rep counts (100kg×5 vs 80kg×10 both represent real effort but aren't directly comparable). The standard fix, used by mainstream lifting apps (Strong, Hevy), is **estimated 1RM** via the Epley formula:

```
e1RM = weight × (1 + reps / 30)
```

For each exercise, the progress chart plots the **best e1RM among non-warmup sets, per session, over time**. This isn't stored — it's computed on the fly from `weight` and `reps`, so the formula can be revisited later without a migration.

Three rules the bare formula doesn't cover:

- **`reps == 1` returns `weight` unchanged.** Epley would report a tested 100kg single as 103.3kg — estimating a measurement, and estimating it upward.
- **Bodyweight exercises chart best reps instead.** Their `weight` is null or holds only added weight, so Epley would either have nothing to multiply or claim a +10kg belt pull-up is a 12kg lift. For `is_bodyweight` exercises the y-axis is reps; belt-loaded sets get charted separately if that ever becomes worth doing.
- **Epley degrades above roughly 12 reps.** The chart still draws high-rep accessory work; it just isn't a number to read closely.

## REST API

```
GET    /health               -- liveness + DB reachability, for Compose and Azure probes

POST   /auth/register        -- gated by INVITE_CODE env var (same pattern as subscription-tracker); rate-limited per IP
POST   /auth/login           -- returns JWT; rate-limited per IP
POST   /auth/change-password -- bumps token_version, returns a fresh token

GET    /exercises?search=    -- autocomplete, scoped to the current user
PATCH  /exercises/{id}       -- rename, and set/clear isBodyweight
GET    /exercises/{id}/history?from=&to=  -- per-session best e1RM (non-warmup sets) for the progress chart

GET    /workouts?limit=&before=   -- pages, newest first: (date desc, startedAt desc)
POST   /workouts                  -- create { date, startedAt, title?, bodyweightKg?, location?, notes? }
GET    /workouts/{id}             -- one page: heading fields + blocks and sets, in position order
PATCH  /workouts/{id}
DELETE /workouts/{id}

PUT    /workouts/{id}/exercises   -- replace the whole session in one transaction; what the "new workout" page saves
POST   /workouts/{id}/sets        -- { exerciseName, weight?, reps, isWarmup } appended to that exercise's block
PATCH  /workouts/{id}/sets/{setId}
DELETE /workouts/{id}/sets/{setId}
```

Sets are nested under `/workouts/{id}` since a set only exists in the context of one workout — this also means the "does this workout belong to the caller" authorization check happens once at the parent route. `WorkoutExercise` deliberately does *not* surface as its own `/workouts/{id}/exercises/{blockId}/sets` path: three levels of nesting to express one entity the user never names is a worse API than one the server keeps consistent on their behalf.

Three behaviours the route list doesn't show on its own:

- **`exerciseName` is get-or-create, twice over.** The name is normalized, an `Exercise` row is found or created for that user, and then the block for it in this workout is found or created (appended at the next `position`). This is the hinge the whole autocomplete design turns on: the client never sends an exercise id or a block id, and there is no `POST /exercises` — exercises come into being by being used. `setNumber` is assigned server-side as the next number in the block, rather than trusted from the client.
- **`PUT /workouts/{id}/exercises` exists because of how the frontend actually saves.** The "new workout" page holds several exercise blocks, each with several set rows, and commits them with one button. Against `POST .../sets` alone that's N sequential round trips with no transaction — a failure halfway through leaves a half-logged session on the server and no clean way for the UI to recover. The bulk route takes the whole block list, replaces the workout's contents atomically, and lets the page's save be one request. The single-set routes stay for incremental edits from the history view.
- **Renaming an exercise onto a name that already normalizes to an existing one merges them:** the losing exercise's blocks are repointed and its row deleted. Without this there is no way to fix a typo'd "Bnech Press" that already has sets attached, since deleting an exercise with history is refused — and typos in a free-text autocomplete field are not an edge case.

## Auth

- Username + password (bcrypt/BCrypt.Net hashing), JWT bearer tokens — same shape as subscription-tracker's `auth.py`, ported to C#.
- Multiple users supported, no email verification.
- Registration gated by a shared `INVITE_CODE` environment variable: unset/empty means registration is open, set means the code must match. `ASP.NET Core Identity` is intentionally skipped in favor of a lean, hand-rolled `User` table + JWT — it's simpler to fully understand and matches the subscription-tracker approach.

### Token lifetime and revocation

- **Access tokens live 30 minutes.** No refresh tokens: this is a single-page app used a few times a week, and a refresh-token rotation scheme is a meaningful amount of security-sensitive machinery to hand-write for a first C# project. The frontend re-prompts for login when a call comes back 401.
- **`token_version` is the revocation mechanism** (see the data model). Every token carries the value it was issued under; `POST /auth/change-password` bumps the row, and every token minted before that stops validating on the next request. This is the only lever there is, given tokens are otherwise stateless and unrevokable.
- **`POST /auth/change-password`** takes the current password and a new one, and returns a freshly minted token so the caller who *did* change the password isn't logged out by their own action.
- **Logout is client-side** — drop the token. Honest about what a stateless JWT can offer: nothing server-side happens, and pretending otherwise would be theatre. `token_version` is the real answer when a session genuinely must be killed.

### Rate limiting

`POST /auth/login` and `POST /auth/register` are rate-limited per IP using the built-in `Microsoft.AspNetCore.RateLimiting` middleware (a fixed window is sufficient). Without it, a username-and-password login endpoint on a public URL is an open invitation to credential stuffing, and the invite code protects registration from *signups*, not from being hammered. In-process counters are the accepted limitation: with one backend replica that's correct, and if this ever scales out, the limiter needs shared state — the same trade-off subscription-tracker recorded and deferred.

### Deliberately out of scope

Password *reset* and email verification are not in this plan, and neither is any email-sending path. That is the same line subscription-tracker drew and then had to walk back: unverified accounts mean the invite code, not verification, is what stands between a public URL and open signup. Worth knowing that's the trade being made, rather than discovering it at deploy time.

## Frontend

- Login / register screen.
- "New workout" page: a heading block (date, start time defaulting to now, optional title, bodyweight, location, notes) + a growable list of exercise blocks, each with an autocomplete input (backed by `GET /exercises?search=`) and a dynamic list of set rows (weight, reps, warm-up toggle, add/remove). The whole page saves as a single `PUT /workouts/{id}/exercises`. A block whose exercise is marked bodyweight hides the weight field unless the user opts into added weight.
- Workout history list: a flat list of pages, newest first, each row showing date, start time and title. Two sessions on one date are two adjacent rows distinguished by their times — no date grouping, since flipping back page by page is what a notebook actually does.
- Per-exercise progress view: line chart of best e1RM per session over time — best reps per session for bodyweight exercises, with the y-axis labelled accordingly.

## Folder structure

```
gymNotes/
├── backend/
│   ├── GymNotes.Api/         # Minimal API endpoints, EF Core models, DbContext, migrations
│   ├── GymNotes.Tests/       # xUnit
│   ├── GymNotes.sln
│   ├── Dockerfile
│   └── entrypoint.sh         # applies migrations, then starts the app
├── frontend/
│   ├── src/
│   ├── Dockerfile
│   └── package.json
├── docker-compose.yml        # local dev: frontend + backend + postgres
├── .github/workflows/
│   ├── test.yml
│   └── build-and-push.yml
├── .env.example
└── README.md
```

Two projects under `backend/` rather than one, because xUnit needs a separate assembly to reference the app from. Splitting further (`.Api` / `.Core` / `.Infrastructure`, the layered-architecture layout most C# tutorials reach for) is deliberately skipped: those boundaries earn their keep on a team-sized codebase and mostly add indirection to a project this size. The migration to them, if it's ever wanted, is a mechanical file move.

## Configuration

All configuration comes from environment variables, with a committed `.env.example` documenting every one of them and a gitignored `.env` holding the real values — the subscription-tracker pattern.

```
POSTGRES_DB=gymnotes
POSTGRES_PASSWORD=devpassword
ConnectionStrings__Default=Host=db;Database=gymnotes;Username=postgres;Password=devpassword
Jwt__Secret=replace-me-with-a-generated-key
Jwt__ExpiryMinutes=30
INVITE_CODE=
CORS_ORIGINS=http://localhost:5173
VITE_API_URL=http://localhost:8080
```

Three things here that will bite otherwise:

- **The double underscore is not a typo.** ASP.NET Core's configuration binder maps `__` in an environment variable onto nested-section separators, so `ConnectionStrings__Default` is what `appsettings.json`'s `{"ConnectionStrings": {"Default": ...}}` reads as. This is the single biggest difference from the flat `DATABASE_URL` habit of the previous project, and a wrong separator fails silently — the app starts with a null connection string rather than complaining.
- **`Host=db`, not `localhost`.** Inside Compose the database is reachable by service name. `VITE_API_URL` and `CORS_ORIGINS` go the other way and use `localhost`, because those addresses are resolved by the *browser*, which is not on the Compose network.
- **`CORS_ORIGINS` is required from the moment the frontend exists.** Vite on `:5173` calling the API on `:8080` is cross-origin, so without the middleware configured, every request fails in the browser while working perfectly from `curl`. Both the local origin and the eventual deployed one live here, comma-separated.

Local development before milestone 2 uses .NET user-secrets rather than a `.env` file, since there's no container to inject environment variables yet and `dotnet user-secrets` keeps the JWT signing key out of the repo by default.

## Testing and CI

Tests exist from milestone 1, not as a later milestone. The plan's own rule — every change via a branch and PR, no direct commits to `main` — only means something if the PR has a check to pass, and branch protection with a required status check is what enforces it mechanically instead of by memory.

**Backend: xUnit, against real Postgres only.** Tests drive the API through `WebApplicationFactory` (in-process, no network) with the database supplied by [Testcontainers](https://dotnet.testcontainers.org/) locally and a GitHub Actions service container in CI, both pinned to the same Postgres major version as `docker-compose.yml`.

Notably this does *not* copy subscription-tracker's two-database matrix. That project runs its suite against both SQLite and Postgres because a bare `pytest` with zero setup is the experience it wants contributors to have. The EF Core equivalent — the InMemory provider — is a worse deal: it enforces no foreign keys, no unique indexes and no real transactions, so the `(user_id, normalized_name)` uniqueness constraint and the cascade rules would all silently "pass" while testing nothing. Those constraints are among the most valuable things in the schema to have covered, so the suite tests the database it actually ships on.

Worth covering specifically, because each is a rule written down in this plan that code can quietly violate:

- e1RM: the `reps == 1` passthrough, and the bodyweight branch returning reps.
- Name normalization: `"Back Squat"`, `"back squat"` and `" Back  squat "` resolving to one `Exercise`, and two different users each keeping their own.
- Ownership: user A gets a 404 on user B's workout, on every route.
- Two workouts on one date coexist, come back in `started_at` order, and appear as two separate points on the progress chart rather than one.
- A session started just after local midnight keeps the `date` the client sent, rather than being re-derived from the UTC instant onto the previous day.
- `token_version`: a token minted before a password change stops working after it.
- The bulk session write rolling back whole rather than half-applying.
- Migrations applying cleanly from empty — the check that catches a model change nobody generated a migration for.

**Frontend:** Vitest for the e1RM/formatting helpers that are shared with the chart. End-to-end coverage is deferred; subscription-tracker's Playwright visual suite is the model if it's ever wanted.

**Workflows:**

- `test.yml` — runs on every push and every PR, and is the required status check on `main`. No `paths:` filter on the trigger: a required check that a path filter prevents from running is one GitHub waits on forever, blocking the merge instead of passing. Skipping the *work* when nothing relevant changed is done with a paths-filter step inside the job, which still reports success. (Learned the hard way in subscription-tracker's `test.yml`, which carries the comment.)
- `build-and-push.yml` — on push to `main`, builds both images and publishes them to GHCR, the registry that comes with the repo and needs no external account. Milestone 6, well before the Azure deploy, so that publishing images and deploying them fail separately and are debugged separately.

## Open items / decisions still to make

- Repo name and GitHub visibility (public/private) — needed before scaffolding.
- .NET version: the current LTS unless there's a reason otherwise. Blocks milestone 1, since it fixes the SDK image the backend `Dockerfile` builds on and the language features available.
- How `is_bodyweight` gets set. Nothing in the log-a-workout flow asks for it, so today it can only be toggled through `PATCH /exercises/{id}`. Options: infer it on first use when a set is saved with no weight, ask once at the moment an exercise is created, or leave it as an edit-after-the-fact — worth settling before milestone 7 rather than after.

## Milestones

1. **Backend first, no Docker** — ASP.NET Core Minimal API skeleton talking to a locally installed Postgres, EF Core + Npgsql wired up and the migration workflow proven end to end (generate, apply, verify) before there's a schema worth losing, plus a health endpoint. The xUnit project and `test.yml` land here too, along with branch protection — so the PR-only rule is enforced from the first PR rather than adopted later.
2. **Containerize the backend** — backend `Dockerfile`, Docker Compose running backend + Postgres, a named volume so data survives restarts, `.env.example`, and pending migrations applied on container start via `entrypoint.sh`.
3. **Auth** — register/login/change-password + JWT middleware, `token_version` checking, rate limiting, invite code.
4. **Core domain** — `Workout`/`WorkoutExercise`/`Exercise`/`SetEntry` EF Core models + migrations, CRUD endpoints including the bulk session write.
5. **Frontend skeleton** — Vite + React + TS, its own `Dockerfile`, added to Compose, CORS configured; login/register UI against milestone 3's endpoints.
6. **Build and publish images** — `build-and-push.yml` pushing both images to GHCR on every push to `main`. Deliberately before the deploy: publishing images and deploying them are separate things that should be able to fail separately.
7. **Log a workout end-to-end** — page heading, add exercise via autocomplete, add sets, save. Includes "finish session" writing `ended_at`.
8. **Progress view** — e1RM calculation + history endpoint + chart on the frontend.
9. **Polish** — workout list/edit/delete, and the exercise rename/merge UI (dedup by `normalized_name` is in the schema from milestone 4; this is the escape hatch for names that were simply typed wrong).
10. **Azure deployment** — Neon Postgres, pulling the images milestone 6 already publishes, OIDC continuous deploy (mirroring subscription-tracker's Azure deployment milestone).

Containers come *second*, not first. This is the one lesson subscription-tracker wrote down explicitly about its own milestone 1: starting without Docker "avoids debugging Docker networking and SQL at the same time." That applies with more force here, since C# and EF Core are both new — a connection that won't open should have one candidate explanation, not three. Migrations are wired up in milestone 1 rather than alongside the domain model, for the other reason that project recorded: it started with `create_all()`, discovered that adding a non-null `user_id` to an existing table isn't something `create_all()` can do, and had to drop the database to move forward.

Auth lands before the domain model on purpose, for the same reason: every `Workout` is scoped to its owner, so building the tables first would mean migrating a non-null `user_id` onto rows that already exist.
