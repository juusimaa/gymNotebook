# GymNotebook Constitution

## Core Principles

### I. Preserve the Established Architecture

Changes MUST preserve the existing .NET 10 ASP.NET Core Minimal API application,
EF Core 10 with Npgsql, PostgreSQL 17, and React + TypeScript with Vite. Keep the
existing API and test projects; do not introduce controllers or an
Application/Domain/Infrastructure split. EF entities retain bare foreign keys
without upward navigation properties. Reuse existing utilities and patterns;
do not add a dependency when the project already solves the problem, or a
single-implementation interface without a concrete reason.

Frontend API calls belong in `src/api/`. Use the existing design tokens and shared
styles, with hand-written CSS and no component library. Preserve the separate
backend and frontend images and the local Compose setup with real PostgreSQL.
These choices keep the learning project understandable at its established size.

Sources: `AGENTS.md` — Stack, Code conventions; `PLAN.md` — Stack, Folder structure;
`README.md` — Stack; `docs/ui/README.md` — Visual direction; `docker-compose.yml`.

### II. Make Focused, Reviewable Changes

Make the smallest coherent change for the task, with no unrelated refactoring.
Work on a branch and deliver changes through reviewable PRs; never commit directly
to `main`. Split large milestones into focused PRs. Present a concise plan before
modifying project files, and obtain the user's approval before creating a PR once
the work is complete and verified.

Sources: `AGENTS.md` — How to work, Code conventions; `PLAN.md` — Goals, Stack.

### III. Keep Learning and Readability Central

The author writing and understanding the code remains the default. AI MAY
implement explicitly delegated, scoped code; that permission does not authorize
unrelated implementation. Otherwise, explain, guide, and review. Code MUST be
readable, with useful comments explaining intent, behavior, and non-obvious
decisions; do not add redundant comments to obvious lines.

Preserve the established conventions: nullable C#, asynchronous I/O with
`CancellationToken`, constructor injection, and no empty catches or bare
`catch (Exception)` outside a real boundary. Use read-only EF queries with
`AsNoTracking()`, projections when only selected fields are needed, and no avoidable
N+1 queries. Use explicit TypeScript types or `unknown` instead of `any`, and handle
loading, empty, and error states.

Sources: `AGENTS.md` — How to work, Code conventions; `PLAN.md` — Goals;
`README.md` — introduction. Comment scope is also explicit in this constitution request.

### IV. Verify Against Real Behavior in Proportion to Risk

Backend integration tests MUST use xUnit, `WebApplicationFactory`, and real
PostgreSQL through Testcontainers, with migrations applied. Do not substitute
EF InMemory or SQLite: foreign keys, uniqueness, cascades, and transactions are
part of what the tests must prove. Keep the test database version aligned with
Compose. Review generated migrations for unintended schema changes before applying
them. Tests MUST be deterministic and independent, follow Arrange/Act/Assert and
`MethodName_Scenario_ExpectedResult`, and MUST NOT be weakened merely to pass.

Use the established formatting, type, lint, and test checks listed below. Match
verification to the affected behavior and risk: ownership and auth changes require
their security behavior to be checked; persistence changes require real-database
evidence; helper changes use the existing Vitest coverage. A documentation-only
change requires document and diff review rather than an invented application test.
This proportionality does not waive existing CI checks. Frontend end-to-end
coverage remains deferred; this constitution does not introduce a new test stack.

Sources: `AGENTS.md` — EF Core, Tests, Commands; `PLAN.md` — Testing and CI;
`README.md` — Tests, Code style; `.editorconfig`; `.github/workflows/test.yml`.
Risk-proportionate verification is explicit in this constitution request.

### V. Preserve Security and Privacy by Design

Keep every exercise and workout operation scoped to its user, including nested
sets. Requests for a specific missing or unowned resource MUST return 404, not
403. Invalid pagination cursors MUST return 400, treating nonexistent and foreign
cursors identically so the response does not reveal another user's resource.
Preserve BCrypt password hashing, JWT bearer authentication and token-version
revocation, the invite-code registration gate, configured CORS origins, and per-IP
rate limiting on login and registration. Do not disable these controls for local
testing. Login failures MUST NOT distinguish an unknown user from a wrong password.

Never log secrets, tokens, or connection strings, commit real secrets, or expose
exception details in API responses. Keep local secrets in the established
gitignored environment file or .NET user-secrets, and production secrets in
Container Apps secrets through `secretref`. Generate production values separately
from development values and set the production invite code: an empty code opens
registration. Preserve development-only OpenAPI/Scalar exposure.

Privacy by design here means preserving these established ownership and
non-disclosure controls. It does not invent retention, analytics, email collection,
or new account-management requirements.

Sources: `AGENTS.md` — Security; `PLAN.md` — Auth, Configuration, API documentation,
Milestone 3; `README.md` — API documentation, Configuration;
`docker-compose.yml`; `.github/workflows/deploy.yml`.

### VI. Keep Behavior and Its Documentation Aligned

When behavior, API shape, or screens change, update `PLAN.md`, `README.md`, and
the relevant `docs/ui/` specification and prototype in the same PR. Keep their
established roles: `PLAN.md` records design, domain/API decisions, and milestones;
`README.md` explains running and checking the project; `docs/ui/` records the UI
design. The prototype remains a reference artifact, not a frontend build input.

Preserve existing domain, API, and UI decisions rather than deriving replacements
from this constitution. Surface contradictory or stale descriptions for review;
documentation alignment is not permission to silently choose a new behavior.

Sources: `AGENTS.md` — Docs; `PLAN.md` — Frontend; `README.md` — introduction;
`docs/ui/README.md` — How this lives in the repo.

### VII. Review Generated Plans and Tasks Before Implementation

Generated plans and tasks MUST be treated as drafts requiring review before
implementation. They must preserve established decisions and show unresolved
questions instead of selecting new architecture, product behavior, APIs, data
models, or UI rules. Generation alone is neither review nor implementation
authorization. AI implementation remains subject to the scoped delegation in
Principle III.

Basis: explicit instruction in this constitution request, alongside the planning
and learning workflow in `AGENTS.md`.

## Source Boundaries and Resolved Questions

This is a brownfield extraction from `AGENTS.md`, `PLAN.md`, `README.md`,
`docs/ui/README.md`, `.editorconfig`, `docker-compose.yml`, and the three workflows
in `.github/workflows/`: `test.yml`, `build-and-push.yml`, and `deploy.yml`.
The user's explicit governance requirements are identified above. Cursor handling
was inspected for Q5, bodyweight editing and interpretation for Q6, and the merge
handler and existing test for Q7. No broader implementation or live deployment
audit was performed; the merge test was inspected, not rerun.

**Q1 — Project status: resolved by owner confirmation on 2026-09-24.**
Milestones 1–10 are complete, including Azure deployment. The status descriptions
in `AGENTS.md`, `PLAN.md`, and `README.md` are stale and need a separate documentation
update; they are unchanged by this constitution-only task. Completion is recorded
from the owner's confirmation, not inferred from `deploy.yml` or a live audit.

**Q2 — Login terminology: resolved by owner confirmation on 2026-09-24.**
Keep the established username/password authentication. References to email login
and preserving email on 401 in `docs/ui/README.md` are stale wording to correct to
username in a separate documentation update. The UI specification is unchanged by
this constitution-only task. Email-based login and password reset remain future
proposals requiring separate review, not current product requirements.

**Q3 — Frontend configuration and image publication: resolved by owner confirmation
on 2026-09-24.** Retain the current implementation as the intended behavior.
Reconcile the older `PLAN.md` and `README.md` descriptions of frontend runtime
configuration, build-time `VITE_API_URL`, action versions, and image tags with the
current environment-specific implementation in a separate documentation update.
Historical milestone accounts must be distinguished from current operating
instructions. This resolution records the owner's acceptance; it does not claim
an implementation audit or authorize configuration, infrastructure, or workflow changes.

**Q4 — UI status and missing states: resolved by owner confirmation on 2026-09-24.**
The UI specification is outdated regarding its proposed status and descriptions
of empty/error/offline states. Reconcile these descriptions with the existing UI
in a separate documentation update. This is a documentation gap, not a finding
that implementation is missing; no implementation or browser audit was performed
to establish coverage of individual states. The UI specification and prototype
remain unchanged by this constitution-only task.

**Q5 — Foreign pagination cursors: resolved by owner approval on 2026-09-24.**
Preserve 404 for missing or unowned resources. Preserve 400 for invalid pagination
cursors, treating nonexistent and foreign cursors identically. This matches the
existing cursor handling in `backend/GymNotebook.Api/Program.cs` and the behavior
recorded in `PLAN.md` milestone 4. Clarify the broad ownership wording in
`AGENTS.md` and `README.md` in a separate documentation update, without changing
API behavior. Only this constitution is updated here.

**Q6 — Bodyweight editing: resolved by owner approval on 2026-09-24.**
The exercise's Bodyweight/Loaded classification is set when creating a bodyweight
exercise through the workout editor and can be changed later through exercise
editing. Both use `PATCH /exercises/{id}`. Record "written only here" in the UI
specification as stale wording to correct in a separate documentation update;
preserve the existing behavior.

This classification (`isBodyweight`) is separate from the lifter's bodyweight in
kilograms (`bodyweightKg`), which is recorded per workout. Recording 82 kg today
does not change a previous workout's recorded 80 kg. The exercise's current
classification applies to past and future workouts' display and progress
calculations without rewriting stored set weights or reps. The current bodyweight
progress metric uses reps, not the lifter's recorded bodyweight in kilograms.

**Q7 — Exercise merge identity: resolved by owner approval on 2026-09-24.**
Preserve the existing merge semantics: the edited exercise keeps its ID and takes
the requested name, the matching exercise's blocks are reassigned to it, and the
duplicate exercise record is removed. Workouts and sets are preserved. This
matches `PLAN.md` milestone 4, the merge handler, and the existing merge test.
Clarify in a separate UI documentation update that "nothing is deleted" means
no workouts or sets are deleted. UI wording must not imply which internal ID
survives. This resolution does not change API or data-model behavior.

All seven source questions are resolved. The documentation follow-ups recorded
above remain separate from this constitution-only task.

## Development Workflow and Review

Before implementation, review the plan/tasks, affected source decisions, scope,
and any relevant unresolved questions. Review changes for the principles above,
including documentation alignment and security boundaries.

Use the existing checks, as applicable locally and as required by CI:

| Area | Established check |
| --- | --- |
| Backend formatting | `dotnet format backend/GymNotebook.sln --verify-no-changes` |
| Backend integration tests | `dotnet test backend/GymNotebook.sln` (Docker required) |
| Frontend types | `npm run typecheck` from `frontend/` |
| Frontend lint | `npm run lint` from `frontend/` |
| Frontend formatting | `npm run format:check` from `frontend/` |
| Frontend helpers | `npm test` from `frontend/` |

`.editorconfig` remains the repository/C# style authority, including UTF-8, LF,
final newline, whitespace rules, and the generated migration formatting exemption.
Frontend formatting and linting retain the Prettier and ESLint setup described
in `README.md`. `test.yml` runs on every push and PR; retain its checks without
adding trigger path filters that leave required checks pending.

Keep build/publication and deployment separate as the workflows establish:
publish both service images on `main`; automatic deployment follows successful
image publication and uses their matching immutable commit tag. Retain explicit
manual redeployment, OIDC authentication, infrastructure-based CORS configuration,
and the deployment health check. These are existing workflow properties, not
evidence of live deployment or new deployment requirements.

## Governance

This constitution records established decisions and the explicit governance
instructions in this request. It does not supersede contradictory source material
by choosing a winner. Resolve relevant questions through owner review before
implementing a decision that depends on them; reconcile affected documents in the
same reviewed change.

Amendments follow the existing branch, concise-plan, verification, and PR-review
workflow. State the changed principle and its basis so the owner can review it.
Plans, tasks, and PR reviews must check the applicable principles; generated
artifacts do not amend the constitution implicitly.

For this Spec Kit artifact, use semantic versioning: major for incompatible
principle changes/removals, minor for added or materially expanded principles,
and patch for non-semantic clarification. Version 1.0.0 was the initial draft;
ratification records the owner's adoption of this constitution, not an
application release.

**Version**: 1.0.1 | **Ratified**: 2026-09-25 | **Last Amended**: 2026-09-25
