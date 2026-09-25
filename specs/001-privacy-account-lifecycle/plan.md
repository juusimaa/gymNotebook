# Implementation Plan: Privacy and Account Lifecycle

**Branch**: `001-privacy-account-lifecycle` | **Date**: 2026-09-24 | **Spec**: [spec.md](spec.md)

**Input**: `specs/001-privacy-account-lifecycle/spec.md`

**Status**: Approved by the owner for implementation, 2026-09-25: proposals P1–P28 approved in [checklists/proposal-review.md](checklists/proposal-review.md) (Q9), constitution ratified (v1.0.1). Restore safety and fail-closed behavior are approved requirements. The restore-evidence direction (pre-restore diff with a log fallback, no ledger) was chosen on 2026-09-24 but is not yet proven; R4 coordination awaits spike Part B, and retention guarantees are not approved. Implementation approval is not operational sign-off: the release gates below still apply.

## Summary

Add a public versioned privacy notice, account-level acknowledgement, password-verified JSON export and permanent account deletion. Preserve the current username/password, invite gate, JWT revocation, notebook model and UI conventions. Notice acknowledgement is never consent. Deliver maintained processing, supplier, retention, rights-request and restore records alongside implementation.

The critical changes are a repeatable-read export, PostgreSQL coordination of account operations and response delivery, atomic active-data deletion, and restore reconciliation that uses the preserved pre-restore Neon branch, with minimal deletion log lines as fallback. Existing JWT validation rejects later requests after deletion but does not protect requests already running. A restored database alone cannot supply evidence of deletions made after its restore point, which is why the preserved branch or the logs are needed (research R6).

See [research.md](research.md), [data-model.md](data-model.md), [API contract](contracts/api.md), [UI contract](contracts/ui.md), [operations contract](contracts/operations.md) and [quickstart.md](quickstart.md). Proposed APIs, schema and mechanisms require owner review under constitution Principle VII. Legal and provider evidence is a release dependency, not something technical research can approve.

## Technical Context

**Language/Version**: C# targeting .NET 10; TypeScript ~6.0.2, React ^19.2.8 and Vite ^8.3.0 as declared in the repository.

**Primary Dependencies**: ASP.NET Core Minimal APIs/JwtBearer 10.0.12, EF Core 10.0.12, Npgsql EF provider 10.0.3, BCrypt.Net-Next 4.2.1, System.Text.Json, React Router ^8.3.1. Reuse installed libraries; no component library or new application layer.

**Storage**: PostgreSQL 17 locally/tests and the existing Neon deployment path; latest acknowledgement and a proposed sign-in suspension marker on User. Restore evidence is the preserved pre-restore Neon branch, with minimal deletion log lines in the existing Container Apps logs as fallback (research R6); no new storage or receipt table. No persistent export files.

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
| III. Learning/readability | User writes implementation by default (constitution 1.x) | Pass: design/contracts only. Since constitution 2.0.0 (2026-09-25), AI implements the tasks unless the owner says they will write a part |
| IV. Real behavior | Plan real-Postgres security/persistence checks | Pass: concurrency, rollback, restore and browser evidence remain distinct; no application checks claimed for this docs-only change |
| V. Security/privacy | Preserve BCrypt/JWT, invite gate, CORS, ownership | Pass: fresh checks under coordination; no client-selected account; legal conclusions remain release gates |
| VI. Documentation alignment | Read PLAN.md, README.md and current UI preview | Pass: require matching operating/API/UI updates with implementation; no unrelated stale-doc cleanup |
| VII. Review artifacts | Specification and plan remain drafts | Pass: proposals are not approved API/schema/UI decisions or implementation authority |

The constitution was ratified on 2026-09-25 (v1.0.1). Ratification does not approve this plan: a draft planning pass is not owner review or production sign-off. There are no constitution exceptions.

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
infra/                          # Retention configuration (for example Log Analytics table retention)
```

**Structure Decision**: Extend the existing API/test/frontend layout. Restore evidence uses the existing database provider and logs, so no storage SDK or application project is added. Evaluate built-in HTTP/identity facilities first; any dependency needs a demonstrated gap and review.

## Phase 0 — Research Outcome

Draft technical proposals cover versioned notice with latest acknowledgement, a single streamed JSON snapshot, transaction-level account locks, fresh delivery authorization and atomic active-data deletion. The account locks and export cancellation (R4) are a candidate design pending the validation spike and open questions below. The owner approved the outcome that restores cannot revive deleted accounts and must fail closed when reconciliation cannot be verified. The restore-evidence direction (pre-restore branch diff with a log fallback, research R6) was chosen on 2026-09-24. It and the numerical retention guarantees remain unapproved pending failure-handling proof and an isolated restore exercise. See research R1–R10.

The restore-evidence and concurrency mechanisms have mandatory implementation proof points. If their tests fail, revise the design rather than relax FR-016/017/020/022. Legal and deployment facts remain explicitly unverified release dependencies, not technical assumptions marked as proven.

## Open Design Questions

Recorded after plan generation. Items marked as blocking must be resolved before `/speckit.tasks` produces tasks for the affected slice; otherwise those tasks would be placeholders. Per constitution Principle VII, these are shown as unresolved rather than decided by generation.

| # | Question | Kind | Blocks tasks? | Owner |
| --- | --- | --- | --- | --- |
| Q1 | Actual Neon/Azure retention: database history/point-in-time window, any other backup or export copies, Log Analytics settings, external font requests, and candidate ledger disposal behavior | Infrastructure evidence | **Answered 2026-09-24**, except the log-content scan and Neon internal copies; see [research R7 → Verified settings](research.md#r7--retention-is-more-than-configuration-intent) | Operator |
| Q2 | Deletion/recovery ledger (R6, [operations.md](contracts/operations.md) Deletion/ledger protocol): how ledger completeness is proven before a restore; what runs reconciliation and expiry cleanup while Container Apps is scaled to zero; how failed cleanup is detected and handled before retention deadlines. Q1 found a 6-hour restore window. **Direction chosen 2026-09-24: pre-restore diff with a log fallback, no ledger** ([research R6](research.md#r6--independent-restore-evidence)). Sub-questions Q2a–Q2c answered and Q2d–Q2e recorded as consequences in R6; dependent documents aligned 2026-09-24 | Design gap | **Answered**; proof remains a release gate | Project owner |
| Q3 | R4 per-request shared guard: transaction advisory locks release at transaction end, so the current token-version check in `OnTokenValidated` cannot hold them. Choose the mechanism (for example request middleware or an endpoint filter owning a per-request transaction), its connection-pool cost, and how existing explicit transactions reuse it | Architecture decision | **Answered 2026-09-24:** endpoint filter on authorized route groups, with a delivery guard for writes and the `OnTokenValidated` early check kept ([research R4](research.md#r4--coordinate-operations-and-response-delivery)) | Project owner |
| Q4 | R4 time bounds: deletion's exclusive-lock wait, write/flush timeout, export chunk size and the A1 p95 latency limit, all within SC-003/SC-005's 60-second budgets | Starting values, tuned by spike Part A | **Answered 2026-09-24:** starting values recorded in [research R4](research.md#r4--coordinate-operations-and-response-delivery) | Author |
| Q5 | R4 wait outcomes: deletion's response when exclusive access is not acquired in time (FR-017 safe retry), and the response to an ordinary request that waited behind a deletion that then committed. Also the login/token-validation response for a suspended account (R6 Q2c) | API behavior | **Answered 2026-09-24:** 503 `temporarily_unavailable` on lock timeout; 401 after a deletion or revocation wins, including a failed write delivery guard; password-first 403 `account_suspended` at login ([research R4](research.md#r4--coordinate-operations-and-response-delivery), [API contract](contracts/api.md)) | Project owner |
| Q6 | R4 login: whether token issuance runs under the shared guard, so a login racing deletion cannot issue a token | Design decision | **Answered 2026-09-24:** no guard; stale login tokens fail the per-request check, proven by a race test ([research R4](research.md#r4--coordinate-operations-and-response-delivery)) | Project owner |
| Q7 | R4 validation spike Part A (local) and Part B (deployed proxy, requires approval of disposable Azure resources) as defined in research R4 | Proof | No; becomes an early blocking task. Part B approved with conditions 2026-09-25 ([research R4](research.md#r4--coordinate-operations-and-response-delivery)) | Author; owner approves Part B |
| Q8 | Controller/contact details, reviewed processing and consent conclusions, supplier evidence, and retention period/location for rights-request records. Also verify the deployed client address seen by the per-IP limiter (unverified finding, research R10) | Owner content | No; release gates below | Project owner/controller |
| Q9 | Review of the proposals: account UUID, endpoints, UI routes and additional storage. Itemized as P1–P28 in [checklists/proposal-review.md](checklists/proposal-review.md) | Review | **Answered 2026-09-24:** all items approved, with decisions recorded in the source documents | Project owner |

**Recommended order:**

1. Q1 first. If the database's own restore window is short and no other copies exist, the independent ledger in Q2 may shrink to a smaller mechanism. If longer-lived copies exist, the ledger is justified and Q2's answers are required. *Done: the window is 6 hours and no other controlled copies were found.*
2. Q2 next, sized to Q1's findings. *Done.*
3. Q3, Q5 and Q6, recording Q4 starting values. Q7 Part A can run alongside. *Done.*
4. Q9, then `/speckit.tasks` and `/speckit.analyze`. *Q9 done; tasks next.* Q7 and Q8 enter as tasks marked as release blockers.

Performance tests, the isolated restore exercise and the owner's mobile/keyboard walkthrough belong to implementation and release. They are not needed to approve the plan.

## Phase 1 — Design and Delivery Boundaries

1. **Processing/publication prerequisites**: inventory collection, legal/health-data review, controller/contact/authority, provider settings and notice. Record evidence gaps with owner and release consequence; do not publish placeholders.
2. **Notice/account controls**: public versioned notice, latest acknowledgement, authenticated controls and deep-link-safe notebook gate. Privacy/contact/export/delete remain available without acknowledgement.
3. **Lifecycle foundation**: reviewed migration, non-reusable account reference, coordination of existing login/password/write paths and personal response delivery. Include the reviewed suspension marker and the Q2d invariant test. Complete this foundation before enabling deletion.
4. **Export**: field guide, deterministic snapshot, authenticated streaming, cancellation and retry states, no persistent artifacts.
5. **Deletion/recovery**: password plus confirmation, atomic active-data removal, all-session invalidation, device cleanup and failure/restore exercises. Deletion log lines and the restore runbook ([operations contract](contracts/operations.md)) follow the direction chosen in research R6, proven by the isolated restore exercise.
6. **Acceptance/release**: performance fixture, required checks, owner walkthrough, legal/provider evidence, retention boundary proof, isolated restore and aligned PLAN.md/README.md/docs/ui/operating records.

These are suggested focused PR boundaries, not tasks or permission to create PRs. Each behavioral PR updates relevant documentation. Keep incomplete features disabled in production until prerequisites pass: automatic main deployment means merging enabled behavior would itself roll it out.

**Production disablement (P25, owner decision 2026-09-24):**

- **The flag:** a backend flag, `PRIVACY_LIFECYCLE_ENABLED`, read at startup next to `INVITE_CODE`. Only the exact value `true` enables it; unset, empty or any other value means disabled. Unlike `INVITE_CODE`, a missing setting fails closed.
- **When disabled:** `/privacy/notice`, `/account/privacy`, `/account/export` and `/account/delete` are not mapped and return 404.
- **Frontend:** it has no flag of its own. A 404 from `GET /account/privacy` means the feature is off, so the UI hides "Privacy & account" and skips the notice gate. Any other error shows the normal error state.
- **Deployment:** the variable is a plain Container Apps environment value in `infra/`, set to `false` until the release gates pass. The implementing PR adds it to README.md and `.env.example`.
- **Scope:** the flag covers the new routes and UI only. The migration (additive columns and UUID backfill) and the R4 endpoint filter ship unflagged, because they preserve behavior and tests prove them. This also allows guard overhead to be measured in production before any user-visible change.

## Release Gates and Evidence Owners

| Gate | Owner | Required evidence | Current status |
| --- | --- | --- | --- |
| Plan/schema/API/UI review and constitution adoption | Project owner | Reviewed decisions and adoption date | Done 2026-09-25: constitution adopted (v1.0.1); plan/schema/API/UI reviewed as P1–P28 (Q9) |
| Lawful basis, possible health data and consent decision | Controller with appropriate reviewer | Dated per-purpose conclusions; amendment if consent needed | Unverified; blocks affected processing |
| Controller/contact/authority and complete notice | Project owner/controller | Final wording and tested monitored contact | Not supplied; blocks publication |
| Hosting/database/logs/network/fonts/support recipients | Operator | Actual inventory, roles, agreements, locations, transfers and settings | Candidates only; blocks affected processing |
| Proposed retention limits, including deletion log lines | Operator | Provider settings, configuration and disposal evidence at all deadlines | Guarantees not approved; unverified; blocks release |
| Restore safety and evidence mechanism | Operator | Preserved-branch diff and log-fallback exercises, crash-point proof and isolated restore exercise | Fail-closed outcome approved; direction chosen 2026-09-24; not yet proven |
| In-flight cancellation and concurrency | Author/reviewer | Two-host Postgres tests and real HTTP/proxy evidence | Spike Part A passed 2026-09-25 (research R4); Part B pending (T053); not implemented |
| Usability, correctness and performance | Project owner | SC-001–007 evidence per quickstart | Not performed |

No live cloud audit, provider-contract review, legal approval or browser QA was performed by this planning command.

## Complexity Tracking

No constitution violations. Added complexity is coordination across HTTP delivery, database deletion and restore evidence; each has a named proof obligation and a fail-closed failure mode.
