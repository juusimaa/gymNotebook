# Gym Notebook working guidelines

Learning project: gym log (workouts → exercises → sets) with an e1RM chart.
Keep changes readable; the author learns C# / ASP.NET Core through planning,
reviewing and approving changes.

## Workflow

- Present a concise implementation plan before editing project files.
- AI implements code, tests, migrations and tooling by default. If the user
  wants to write a piece, explain and review it instead.
- Work on a separate Git branch; never commit directly to `main`.
- Make the smallest coherent change; avoid unrelated refactoring. Split large
  milestones into focused PRs. After verification, ask approval before creating a PR.
- Comment intent, behavior and non-obvious decisions where useful for learning.
- Feature work uses Spec Kit under `specs/`. Read the relevant spec/tasks.
- Read relevant `PLAN.md` sections before changing the data model or API.
  Update affected `PLAN.md`, `README.md` and `docs/ui/` docs in the same PR
  when behavior, API or screens change; keep UI specs and prototype in sync.

## Context and output efficiency

- Search with `rg`/`rg --files`, then read only relevant sections. Reuse context;
  reread only when files may have changed or verification requires it.
- Batch independent searches/checks; keep tool output focused. Summarize results
  and failures instead of dumping full logs or repeating plans and file contents.
- Keep explanations and diffs concise; include decisions, checks and blockers.
- Run checks appropriate to the change plus required checks. Repeat only after
  changes, failures or new evidence; never skip necessary verification to save tokens.
- Use `PLAN.md` and relevant `specs/` for current status; avoid duplicating it here.

## Stack and code rules

.NET 10 / ASP.NET Core Minimal APIs, EF Core 10 + Npgsql, PostgreSQL 17,
BCrypt/JWT auth; React + TypeScript + Vite, hand-written CSS, no component library.
Dockerfiles per service; Compose for local dev; GitHub Actions checks pushes/PRs.
Rules apply to implementation and review.

- **Architecture:** follow `PLAN.md`; no Application/Domain/Infrastructure layers
  or controllers. EF entities use bare foreign keys, no upward navigation properties.
  Reuse utilities/patterns; avoid unjustified single-implementation interfaces.
  Add no dependency when existing code/dependencies solve the problem.
- **C#:** nullable reference types, constructor DI, async I/O with
  `CancellationToken`; no `.Result`/`.Wait()`, empty catches or bare
  `catch (Exception)` outside a real boundary.
- **EF Core:** async APIs, `AsNoTracking()` for reads, projections when only some
  fields are needed; avoid N+1 queries. Review migrations before applying.
- **Frontend:** avoid `any`; use `unknown` or explicit types. Reuse
  `frontend/src/styles/` tokens/classes; API calls belong in `frontend/src/api/`.
  Handle loading, empty and error states.
- **Security:** never log secrets, tokens or connection strings, or expose
  exception details in responses. Other users' `/exercises` and `/workouts`
  data must return 404, not 403. Never disable auth, CORS or rate limiting for tests.
- **Tests:** xUnit + Testcontainers with real Postgres only (no InMemory);
  Vitest for frontend helpers. Arrange/Act/Assert, deterministic, independent;
  name `MethodName_Scenario_ExpectedResult`. Never weaken/remove tests to pass.

## Commands

```sh
# Backend tests require Docker (Testcontainers Postgres)
dotnet test backend/GymNotebook.sln
dotnet format backend/GymNotebook.sln

# From frontend/
npm run typecheck
npm run lint
npm run format:check
npm test

# Full local stack
docker compose up --build
```

## Map and references

- `backend/GymNotebook.Api/`: endpoints, EF models, `Data/AppDbContext`, migrations.
- `backend/GymNotebook.Tests/`: `GymNotebookFactory` uses WebApplicationFactory + Testcontainers.
- `frontend/src/`: `api/`, `auth/`, `screens/`, `styles/`, `routes.tsx`.
- `docs/ui/`: UI specification, tokens and clickable HTML prototype.
- `PLAN.md`: design, data model, API contract and milestone status.
- `README.md`: run/build/test, SDK-only setup and Scalar/OpenAPI documentation.
