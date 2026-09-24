# Implementation Plan: Privacy and Account Lifecycle

**Branch**: `001-privacy-account-lifecycle` | **Date**: 2026-09-24 | **Spec**: [spec.md](spec.md)

**Input**: `specs/001-privacy-account-lifecycle/spec.md`

**Status**: Draft for owner review. Restore safety and fail-closed behavior are approved requirements; the ledger protocol and retention guarantees are not approved. Phase 0 research and Phase 1 design only; no implementation authorization or operational sign-off.

## Summary

Add a public versioned privacy notice, account-level acknowledgement, password-verified JSON export and permanent account deletion. Preserve the current username/password, invite gate, JWT revocation, notebook model and UI conventions. Notice acknowledgement is never consent. Deliver maintained processing, supplier, retention, rights-request and restore records alongside implementation.

The critical changes are a repeatable-read export, PostgreSQL coordination of account operations and response delivery, atomic active-data deletion, and independent restore-suppression evidence. Existing JWT validation rejects later requests after deletion but does not protect requests already running. A restored database cannot supply evidence of deletions made after its backup.

See [research.md](research.md), [data-model.md](data-model.md), [API contract](contracts/api.md), [UI contract](contracts/ui.md), [operations contract](contracts/operations.md) and [quickstart.md](quickstart.md). Proposed APIs, schema and mechanisms require owner review under constitution Principle VII. Legal and provider evidence is a release dependency, not something technical research can approve.

## Technical Context

**Language/Version**: C# targeting .NET 10; TypeScript ~6.0.2, React ^19.2.8 and Vite ^8.3.0 as declared in the repository.

**Primary Dependencies**: ASP.NET Core Minimal APIs/JwtBearer 10.0.12, EF Core 10.0.12, Npgsql EF provider 10.0.3, BCrypt.Net-Next 4.2.1, System.Text.Json, React Router ^8.3.1. Reuse installed libraries; no component library or new application layer.

**Storage**: PostgreSQL 17 locally/tests and the existing Neon deployment path; latest acknowledgement on User; short-lived transactional deletion receipts. Proposed independent private Azure Blob ledger outside the notebook restore boundary. This is a new infrastructure dependency for review, not a verified resource. No persistent export files.

**Testing**: xUnit + WebApplicationFactory + Testcontainers PostgreSQL 17, existing Vitest helpers, real HTTP/proxy cancellation checks, isolated restore exercise and recorded owner mobile/keyboard walkthrough. No new browser test framework.

**Target Platform**: Existing Linux API/frontend containers, Azure Container Apps deployment and mobile/desktop browsers. Bicep declares API maxReplicas 1; live settings were not audited.

**Project Type**: Existing full-stack web application plus maintained operator documents.

**Performance Goals**: SC-003/005: export and deletion each within 60 seconds for 1,000 workouts × 10 blocks × 10 sets under recorded normal load/connection. SC-004: each owner walkthrough flow under three minutes excluding download. Record actual resources, latency, payload size and peak memory; no invented user-count target.

**Constraints**: Preserve ownership 404s and invalid-cursor 400s; no credential export/logging; no fabricated acknowledgement; all sessions invalidated on deletion; no silent truncation. Product limits: temporary exports ≤24 hours, backups ≤30 calendar days after deletion, identifying logs ≤30 days after collection, deletion evidence ≤31 days after deletion. Copy/restore cannot restart clocks. No consent flow without a reviewed specification amendment.

**Scale/Scope**: Five P1 stories; public notice and account privacy/export/delete flows; latest acknowledgement; coordinated account operations; operational records and restore validation. Existing training features retain their behavior.

## Constitution Check

Planning gates were checked before research and again against this design. Both checks pass for draft design work; release gates below remain blocked pending evidence.

| Principle | Pre-research check | Post-design check |
| --- | --- | --- |
| I. Established architecture | Existing projects, EF and Minimal APIs | Pass: direct EF projections/transactions, plain entities and concrete helpers; no repository layers or unnecessary interfaces |
| II. Focused changes | Clean feature branch; plan announced before setup | Pass: only feature planning documents generated; focused delivery boundaries below |
| III. Learning/readability | User writes implementation by default | Pass: design/contracts only; implementation still requires hand-written checkpoints or scoped delegation |
| IV. Real behavior | Plan real-Postgres security/persistence checks | Pass: concurrency, rollback, restore and browser evidence remain distinct; no application checks claimed for this docs-only change |
| V. Security/privacy | Preserve BCrypt/JWT, invite gate, CORS, ownership | Pass: fresh checks under coordination; no client-selected account; legal conclusions remain release gates |
| VI. Documentation alignment | Read PLAN.md, README.md and current UI preview | Pass: require matching operating/API/UI updates with implementation; no unrelated stale-doc cleanup |
| VII. Review artifacts | Constitution/specification remain drafts | Pass: proposals are not approved API/schema/UI decisions or implementation authority |

Constitution ratification remains pending owner review. A draft planning pass is not ratification or production sign-off. There are no constitution exceptions.

## Project Structure

### Documentation (this feature)

```text
specs/001-privacy-account-lifecycle/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
└── contracts/
    ├── api.md
    ├── ui.md
    └── operations.md
```

`tasks.md` belongs to speckit-tasks and is not generated here.

### Source Code (repository root)

Existing paths and proposed additions for subsequent implementation:

```text
backend/GymNotebook.Api/
├── Program.cs                   # Existing routes/auth; lifecycle integration
├── User.cs                      # Proposed account UUID and latest notice fields
├── AccountLifecycle.cs          # Proposed concrete coordination helper
├── PrivacyEndpoints.cs          # Proposed Minimal API mapping, not controllers
├── NotebookExport.cs            # Proposed snapshot/field guide and streamed result
├── DeletionReceipt.cs           # Proposed short-lived EF entity
├── Data/AppDbContext.cs
└── Migrations/                  # Reviewed migration after schema approval
backend/GymNotebook.Tests/       # Existing project; privacy/concurrency coverage
frontend/src/
├── api/client.ts                # Download support and observed invalidation
├── api/privacy.ts               # Proposed wire types and calls
├── auth/requireAuth.ts          # Auth guard plus separate notebook gate
├── routes.tsx
├── screens/                    # Proposed notice/account/export/delete screens
└── styles/                     # Reuse tokens/shared rules
frontend/index.html              # Discovered external font requests
docs/privacy/                   # Proposed reviewed notice/operating records
docs/ui/                        # Existing specification and prototype
infra/                          # Retention configuration and independent ledger
```

**Structure Decision**: Extend the existing API/test/frontend layout. The independent ledger addresses a restore boundary, not a reason for another application project. Evaluate built-in HTTP/identity facilities before adding a storage SDK; a dependency needs a demonstrated gap and review.

## Phase 0 — Research Outcome

Draft technical proposals cover versioned notice with latest acknowledgement, a single streamed JSON snapshot, transaction-level account locks, fresh delivery authorization and atomic active-data deletion. The owner approved the outcome that restores cannot revive deleted accounts and must fail closed when reconciliation cannot be verified. The independent receipt protocol and numerical retention guarantees remain unapproved pending provider-capability evidence, failure-handling proof and an isolated restore exercise. See research R1–R10.

Candidate ledger and concurrency mechanisms have mandatory implementation proof points. If their tests fail, revise the design rather than relax FR-016/017/020/022. Legal and deployment facts remain explicitly unverified release dependencies, not technical assumptions marked as proven.

## Phase 1 — Design and Delivery Boundaries

1. **Processing/publication prerequisites**: inventory collection, legal/health-data review, controller/contact/authority, provider settings and notice. Record evidence gaps with owner and release consequence; do not publish placeholders.
2. **Notice/account controls**: public versioned notice, latest acknowledgement, authenticated controls and deep-link-safe notebook gate. Privacy/contact/export/delete remain available without acknowledgement.
3. **Lifecycle foundation**: reviewed migration, non-reusable account reference, coordination of existing login/password/write paths and personal response delivery. Review any proposed receipt infrastructure before implementation. Complete this foundation before enabling deletion.
4. **Export**: field guide, deterministic snapshot, authenticated streaming, cancellation and retry states, no persistent artifacts.
5. **Deletion/recovery**: password plus confirmation, atomic active-data removal, all-session invalidation, device cleanup and failure/restore exercises. Select and review the independent restore-evidence mechanism after the capability and failure review; prepared receipts and ledger completion are candidate details, not authorized implementation tasks.
6. **Acceptance/release**: performance fixture, required checks, owner walkthrough, legal/provider evidence, retention boundary proof, isolated restore and aligned PLAN.md/README.md/docs/ui/operating records.

These are suggested focused PR boundaries, not tasks or permission to create PRs. Each behavioral PR updates relevant documentation. Keep incomplete features disabled in production until prerequisites pass: automatic main deployment means merging enabled behavior would itself roll it out.

## Release Gates and Evidence Owners

| Gate | Owner | Required evidence | Current status |
| --- | --- | --- | --- |
| Plan/schema/API/UI review and constitution adoption | Project owner | Reviewed decisions and adoption date | Pending |
| Lawful basis, possible health data and consent decision | Controller with appropriate reviewer | Dated per-purpose conclusions; amendment if consent needed | Unverified; blocks affected processing |
| Controller/contact/authority and complete notice | Project owner/controller | Final wording and tested monitored contact | Not supplied; blocks publication |
| Hosting/database/logs/network/fonts/support recipients | Operator | Actual inventory, roles, agreements, locations, transfers and settings | Candidates only; blocks affected processing |
| Proposed retention limits, including receipt copies | Operator | Provider settings, configuration and disposal evidence at all deadlines | Guarantees not approved; unverified; blocks release |
| Restore safety and evidence mechanism | Operator | Provider capability review, crash-point proof and isolated restore exercise | Fail-closed outcome approved; ledger protocol not approved |
| In-flight cancellation and concurrency | Author/reviewer | Two-host Postgres tests and real HTTP/proxy evidence | Not implemented |
| Usability, correctness and performance | Project owner | SC-001–007 evidence per quickstart | Not performed |

No live cloud audit, provider-contract review, legal approval or browser QA was performed by this planning command.

## Complexity Tracking

No constitution violations. Added complexity is coordination across HTTP delivery, database deletion and restore evidence; each has a named proof obligation and a fail-closed failure mode.
