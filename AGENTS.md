# Project working guidelines

## What this is

Gym Notebook is a full-stack **learning project**: a digital replacement for a
paper gym log (workouts → exercises → sets, with an e1RM progress chart). The
explicit goal is for the author to write the code by hand and learn C# / ASP.NET
Core along the way — see [PLAN.md](PLAN.md) for the design rationale, data
model, API contract and milestone log, and [README.md](README.md) for the
"how do I run it" companion.

## Stack at a glance

- **Backend:** .NET 10, ASP.NET Core Minimal APIs, EF Core 10 + Npgsql, hand-rolled BCrypt/JWT auth.
- **Database:** PostgreSQL 17.
- **Frontend:** React + TypeScript (Vite), hand-written CSS against `docs/ui/` design tokens — no component library.
- **Tests:** xUnit + Testcontainers (real Postgres, no in-memory provider) on the backend; Vitest for frontend helpers.
- **Containers/CI:** one Dockerfile per service, Docker Compose for local dev, GitHub Actions runs format + tests on every push/PR.

## How to work in this repo

- **The user writes the implementation code.** This is a hand-typing learning
  project, so don't generate or write code changes unless explicitly asked to
  in that instance. Default instead to: explain the plan/design, point to the
  relevant files or patterns, answer questions, and review code the user
  writes. Non-code tasks — running `dotnet ef migrations add`, running tests
  or builds, git/PR mechanics, research — are fine to do directly, since the
  goal is about typing the *code* by hand, not the surrounding tooling. If
  it's unclear whether something counts as code the user wants to type
  themselves, ask rather than assume.
- The default branch is `main`. Do project work on a separate Git branch;
  never commit directly to `main`.
- Before writing or modifying project files, present a concise implementation
  plan to the user first.
- Document the code clearly — this is also an educational project. Add useful
  comments that explain intent, behavior, and non-obvious decisions.
- Split large milestones into multiple focused, reviewable pull requests.
- When work is complete and verified, ask the user for approval before
  creating a pull request.

## Code conventions & review standards

These apply when reviewing code the user writes, and — for the narrow cases
where you do write code yourself (tests, migrations, tooling; see above) —
when writing it.

- Make the smallest coherent change for the task; no unrelated refactoring.
- Follow the architecture PLAN.md already chose: no layered
  `Application`/`Domain`/`Infrastructure` split (deliberately skipped, see
  [PLAN.md → Folder structure](PLAN.md#folder-structure)), bare foreign keys
  without upward navigation properties on EF entities, Minimal API endpoints
  rather than controllers.
- Reuse existing utilities and patterns before adding new ones; avoid an
  interface with a single implementation unless it earns its keep.
- Don't add a dependency (NuGet or npm) when something already in the project
  solves the problem.

**C#:** nullable reference types; async all the way — no `.Result`/`.Wait()`;
propagate `CancellationToken` on I/O; constructor DI; no empty `catch` blocks
or bare `catch (Exception)` outside a real boundary.

**EF Core:** async APIs, `AsNoTracking()` on read-only queries, projections
instead of loading whole entities when only a few fields are needed, no
avoidable N+1 round-trips. Review a generated migration for unintended
schema changes before applying it. The suite runs against real Postgres only
— no InMemory provider (see [PLAN.md → Testing and CI](PLAN.md#testing-and-ci))
— so a query that only "works" against a fake provider isn't proven.

**Frontend:** TypeScript, avoid `any` (prefer `unknown` or an explicit type);
reuse the existing tokens/classes in `src/styles/` rather than one-off CSS —
there is no component library by design; keep API calls in `src/api/`;
handle loading/empty/error states.

**Security:** never log secrets, tokens, or connection strings; preserve
ownership checks (every `/exercises` and `/workouts` route must 404, not
403, on another user's data); don't disable auth, CORS, or rate limiting to
make local testing easier; don't leak exception details in API responses.

**Tests:** Arrange/Act/Assert; name as `MethodName_Scenario_ExpectedResult`;
deterministic and independent; don't weaken or remove a test just to make it
pass.

**Docs:** when behavior, the API shape, or a screen changes, update
PLAN.md/README.md/docs/ui in the same PR — this is already how the project
works (see PLAN.md's Frontend section on keeping the UI spec and prototype
in sync).

## Commands

```sh
# Backend tests (needs Docker running — Testcontainers spins up real Postgres)
dotnet test backend/GymNotebook.sln

# Backend formatting
dotnet format backend/GymNotebook.sln

# Frontend checks (from frontend/)
npm run typecheck     # tsc -b
npm run lint          # eslint, type-aware rules on
npm run format:check  # prettier
npm test              # vitest — helper tests only

# Run the full stack locally
docker compose up --build
```

See [README.md](README.md#running-it) for SDK-only dev setup (no Compose) and
[README.md#api-documentation](README.md#api-documentation) for the Scalar/OpenAPI docs.

## Where things live

```
backend/GymNotebook.Api/     Minimal API endpoints, EF Core models, Data/AppDbContext, Migrations/
backend/GymNotebook.Tests/   xUnit; GymNotebookFactory = WebApplicationFactory + Testcontainers Postgres
frontend/src/                React app (api/, auth/, screens/, styles/, routes.tsx)
docs/ui/                     UI specification + clickable HTML prototype
PLAN.md                      design, decisions and milestone log — read before touching the data model or API shape
README.md                    run/build/test instructions
```

## Current status

Milestones 1–6 are done (backend, frontend and CI/CD are containerized and
publish to GHCR). Milestone 7 — logging a workout end-to-end — is next. See
[PLAN.md → Milestones](PLAN.md#milestones) for the full list and what each one
turned out to involve.
