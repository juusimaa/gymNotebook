# Gym Notebook — Project Plan

A learning project to build a digital replacement for a paper gym log/notebook: track workouts (exercises, sets, reps, weight) and visualize strength progress over time.

## Goals

- Full-stack educational project — type as much code as possible by hand.
- Backend in **C#** (new for this author — previous project used Python/FastAPI).
- Mirror the successful patterns from the `docker-subscription-tracker` project: devcontainer, Docker Compose, PR-only workflow, eventual Azure deployment via Neon Postgres.

## Stack

- **Backend:** ASP.NET Core Web API + Entity Framework Core + Npgsql
- **Frontend:** React + TypeScript (Vite)
- **Database:** PostgreSQL (Neon for the eventual Azure deploy, same as subscription-tracker)
- **Containers:** devcontainer + Docker Compose, following the subscription-tracker pattern
- **Hosting:** Azure (later milestone, once the app works locally)
- **Workflow:** every change via a branch + PR, no direct commits to `main`

## Domain background: what a gym notebook actually contains

A paper training log is organized by session, then by exercise, then by set:

```
Date: 2026-09-10

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

## Data model

```
User        (id, username, password_hash, created_at)
Exercise    (id, user_id, name, created_at)          -- free-text, reused via autocomplete
Workout     (id, user_id, date, notes?)
SetEntry    (id, workout_id, exercise_id, set_number, weight, reps, is_warmup)
```

- `Exercise` is per-user free text (e.g. "Back Squat"), captured once and offered back via autocomplete on later entries — no fixed/global exercise list.
- `is_warmup` flags sets that shouldn't count toward the progress metric (see below).

## Progress metric: estimated one-rep max (e1RM)

Raw weight isn't comparable across different rep counts (100kg×5 vs 80kg×10 both represent real effort but aren't directly comparable). The standard fix, used by mainstream lifting apps (Strong, Hevy), is **estimated 1RM** via the Epley formula:

```
e1RM = weight × (1 + reps / 30)
```

For each exercise, the progress chart plots the **best e1RM among non-warmup sets, per session, over time**. This isn't stored — it's computed on the fly from `weight` and `reps`, so the formula can be revisited later without a migration.

## REST API

```
POST   /auth/register        -- gated by INVITE_CODE env var (same pattern as subscription-tracker); no email verification
POST   /auth/login           -- returns JWT

GET    /exercises?search=    -- autocomplete, scoped to the current user
GET    /exercises/{id}/history  -- per-session best e1RM (non-warmup sets) for the progress chart

GET    /workouts             -- list, most recent first
POST   /workouts             -- create { date, notes? }
GET    /workouts/{id}
PATCH  /workouts/{id}
DELETE /workouts/{id}

POST   /workouts/{id}/sets       -- { exerciseName, setNumber, weight, reps, isWarmup }
PATCH  /workouts/{id}/sets/{setId}
DELETE /workouts/{id}/sets/{setId}
```

Sets are nested under `/workouts/{id}` since a set only exists in the context of one workout — this also means the "does this workout belong to the caller" authorization check happens once at the parent route.

## Auth

- Username + password (bcrypt/BCrypt.Net hashing), JWT bearer tokens — same shape as subscription-tracker's `auth.py`, ported to C#.
- Multiple users supported, no email verification.
- Registration gated by a shared `INVITE_CODE` environment variable: unset/empty means registration is open, set means the code must match. `ASP.NET Core Identity` is intentionally skipped in favor of a lean, hand-rolled `User` table + JWT — it's simpler to fully understand and matches the subscription-tracker approach.

## Frontend

- Login / register screen.
- "New workout" page: date picker + a growable list of exercise blocks, each with an autocomplete input (backed by `GET /exercises?search=`) and a dynamic list of set rows (weight, reps, warm-up toggle, add/remove).
- Workout history list.
- Per-exercise progress view: line chart of best e1RM per session over time.

## Open items / decisions still to make

- Repo name and GitHub visibility (public/private) — needed before scaffolding.
- Whether `Exercise` names should be case/whitespace-normalized for dedup (e.g. "back squat" vs "Back Squat") before autocomplete matching.

## Milestones

1. Repo scaffold: devcontainer, ASP.NET Core Web API skeleton, Vite+React+TS skeleton, Docker Compose, empty Postgres wired up.
2. Auth: register/login endpoints + JWT middleware, login/register UI.
3. Core domain: `Workout`/`Exercise`/`SetEntry` EF Core models + migrations, CRUD endpoints.
4. Frontend: log a workout end-to-end (add exercise via autocomplete, add sets, save).
5. Progress view: e1RM calculation + history endpoint + chart on the frontend.
6. Polish: workout list/edit/delete, exercise autocomplete dedup.
7. Azure deployment: Neon Postgres, container registry, OIDC continuous deploy (mirroring subscription-tracker's Azure deployment milestone).
