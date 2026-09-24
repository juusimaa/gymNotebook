<!--
Sync Impact Report — temporary review material; remove before committing.
Version change: unfilled template -> 1.0.0 (initial brownfield constitution draft).
Principles: five template slots replaced by seven source-grounded principles.
Added sections: Source Boundaries and Unresolved Questions; Development Workflow
and Review; Governance.
Removed sections: none; illustrative template text removed.
Only this constitution is changed; dependent templates are not modified.
Deferred: TODO(RATIFICATION_DATE), pending owner review; unresolved questions Q1–Q7.
Risk-proportionate verification, focused AI comments, and review of generated
plans/tasks are explicit instructions for this constitution, not claims that
all source documents already state them verbatim.
-->

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
sets. Access to another user's resource MUST return 404 rather than disclose its
existence with 403; the pagination wording discrepancy is recorded as Q5 below.
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

## Source Boundaries and Unresolved Questions

This is a brownfield extraction from `AGENTS.md`, `PLAN.md`, `README.md`,
`docs/ui/README.md`, `.editorconfig`, `docker-compose.yml`, and the three workflows
in `.github/workflows/`: `test.yml`, `build-and-push.yml`, and `deploy.yml`.
The user's explicit governance requirements are identified above. No application
implementation or live deployment was audited to settle discrepancies.

The following questions remain unresolved; none authorizes a behavior change:

- **Q1 — Project status:** `AGENTS.md` says milestone 7 is next; `README.md` and
  `PLAN.md` record milestones 1–9 complete and Azure as next. `deploy.yml` already
  defines automatic deployment after successful image publication and manual
  redeployment. What completion status should these documents record? A workflow
  definition alone does not establish successful deployment.
- **Q2 — Login terminology:** `docs/ui/README.md` describes email login and
  preserving email on 401; `PLAN.md` and `README.md` describe username/password
  authentication without email verification. How should the UI description be
  reconciled with the documented authentication contract?
- **Q3 — Frontend configuration and image publication:** `README.md` says runtime
  configuration will arrive in milestone 6, while that milestone is recorded as
  complete. `PLAN.md` describes runtime `config.js` but its milestone 5 account and
  Compose describe build-time `VITE_API_URL`. Milestone 6 also records older action
  versions and `sha-<commit>` tags, while `build-and-push.yml` emits full
  `main-<commit>` tags consumed by `deploy.yml`. Which passages are historical, and
  which need updating to describe the current environment-specific setup?
- **Q4 — UI status and missing states:** the UI spec is labelled proposed and says
  empty/error/offline states are not drawn; `PLAN.md` says the UI is implemented
  with loading/empty/error states and aligned with the prototype. Which reference
  states and status labels remain to be reconciled?
- **Q5 — Foreign pagination cursors:** `AGENTS.md` and `README.md` broadly require
  404 for another user's data on workout routes, while `PLAN.md` milestone 4 says
  an unknown or foreign `before` cursor returns 400. How should the documented
  ownership rule and cursor-validation behavior be reconciled without changing
  either by inference?
- **Q6 — Bodyweight editing:** the UI spec's implementation notes say
  `is_bodyweight` is written only on the exercise edit screen, while its new-page
  section and `PLAN.md` also describe a follow-up PATCH for newly created
  bodyweight exercises. How should the exclusive wording be corrected?
- **Q7 — Exercise merge identity:** the UI edit description says the old name
  disappears and sessions move to the existing name; `PLAN.md` milestone 4 says
  the PATCHed row survives and the colliding row is deleted. Which identity and
  movement semantics should the descriptions communicate? Do not infer a new
  survivor-ID contract from the UI wording.

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
and patch for non-semantic clarification. Version 1.0.0 denotes the initial
constitution draft, not a new application release or a claim of owner approval.

TODO(RATIFICATION_DATE): record the adoption date after owner review; no prior
ratification date is established by the source documents.

**Version**: 1.0.0 | **Ratified**: pending owner review | **Last Amended**: 2026-09-24
