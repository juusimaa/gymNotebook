# Proposal Review Checklist (Q9): Privacy and Account Lifecycle

**Purpose**: Owner review of every proposal in the plan before `/speckit.tasks`. This is [plan.md → Open Design Questions](../plan.md#open-design-questions) item Q9.
**Created**: 2026-09-24
**Feature**: [spec.md](../spec.md), [plan.md](../plan.md)
**How to use**:
- Tick an item to approve it as written.
- To change an item, write the change in its **Decision** line and leave it unticked until the source documents are updated.
- Items marked *Decided* were already chosen by the owner on 2026-09-24. Tick them to confirm there is no change on reflection.
- A tick approves the design, not an implementation or a legal conclusion.

## Data model

- [x] **P1 — PrivacyAccountId.** Add a UUID to User: non-null, unique, immutable, server-generated and backfilled for existing accounts. Integer keys and JWT claims stay as they are. It is used for restore reconciliation and is exported as account identity.
  Source: [data-model.md → User additions](../data-model.md#user-additions), [research R5](../research.md#r5--removal-and-identity).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P2 — Latest-only notice acknowledgement.** Add `AcknowledgedPrivacyNoticeVersion` (bounded string, proposed max 64) and `PrivacyNoticeAcknowledgedAt` to User. Both are null or both are set. No history is kept and there is no consent flag.
  Source: [data-model.md](../data-model.md#user-additions), [research R2](../research.md#r2--notice-and-acknowledgement).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P3 — SignInSuspendedAt.** A nullable timestamp on User, set only by the restore fallback when a deletion's outcome is unknown. It blocks login with 403 `account_suspended`, is excluded from export, and is cleared by the owner through the contact path. *Explicitly flagged for this review.*
  Source: [data-model.md](../data-model.md#user-additions), [research R6 → Q2c](../research.md#r6--independent-restore-evidence).
  **Decision:** Approved with clarifications, 2026-09-24. The operator sets and clears it by documented manual SQL, with no API. Login returns the password-first 403, and `OnTokenValidated` rejects it as a backstop. It stays excluded from export. Resolution is to confirm with the user, then delete or clear.
- [x] **P4 — Notice versions as repository artifacts.** Versioned notice content lives in `docs/privacy/notices/` with a current-version pointer. It is not an EF entity.
  Source: [data-model.md → PrivacyNoticeVersion](../data-model.md#privacynoticeversion--repository-artifact-not-ef-entity).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P5 — No receipt or export-history tables.** *Decided* (Q2a). Nothing is persisted for completed exports or deletions.
  **Decision:** Approved as written, 2026-09-24.

## HTTP API

- [x] **P6 — `GET /privacy/notice`.** Public, with no side effects. Returns the current notice plus optional announced-successor metadata.
  Source: [api.md → Endpoints](../contracts/api.md#endpoints).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P7 — `GET /account/privacy`.** Bearer. Returns the current notice version, the latest acknowledgement and `requiresAcknowledgement`.
  **Decision:** Approved as written, 2026-09-24.
- [x] **P8 — `PUT /account/privacy/acknowledgement`.** Bearer. Accepts the current version only and is idempotent; a stale version returns 409 `notice_version_changed`.
  **Decision:** Approved as written, 2026-09-24.
- [x] **P9 — `POST /account/export`.** Bearer plus the current password in a JSON body. Returns a streamed `gym-notebook-export.json` attachment in format version 1, with no public link, no 202 and no resume.
  Source: [api.md → Export format version 1](../contracts/api.md#export-format-version-1).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P10 — `POST /account/delete`.** Bearer, `currentPassword` and `confirmDeletion: true`. Returns 200 with `status`, `retentionBoundaryAt`, `backupsExpireBy`, `deletionEvidenceExpiresBy` and `logRetentionNotice`. POST is used rather than DELETE because of the body.
  Source: [api.md → Deletion response](../contracts/api.md#deletion-response).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P11 — New status codes.**
  - 400 `password_verification_failed` for a wrong password on the new operations; login keeps its 401.
  - 415 for non-JSON bodies.
  - 503 `temporarily_unavailable` for lock timeouts (*Decided*, Q5).
  - 503 `deletion_outcome_unknown`.
  - 403 `account_suspended` at login (*Decided*, Q5).
  - 401 after a deletion or revocation wins (*Decided*, Q5).
  **Inconsistency fixed:** [research R9](../research.md#r9--ui-state-and-errors) now names the password-first 403 as the one exception to login's generic 401.
  **Decision:** Approved as written, 2026-09-24.
- [x] **P12 — Throttling and export concurrency.** The existing per-IP auth limiter, plus a per-account bucket of 10 attempts per 60 seconds with no queue, and one export stream per account at a time.
  **Resolved:** see [research R10 → P12 decision](../research.md#r10--throttling-and-validation).
  **Decision:** Export limit uses an export-namespace advisory lock on the snapshot transaction. The password throttle stays in process with a documented per-instance bound. The per-IP forwarded-headers issue is recorded as a separate unverified finding. 2026-09-24.

## Coordination (R4)

- [x] **P13 — Endpoint filter guard.** *Decided* (Q3). Transaction advisory locks through a filter on the authorized groups; reads hold the lock through the write; writes commit, then deliver under a guard. Export opts out, and a coverage test checks every authorized endpoint.
  Source: [research R4 → Q3 decision](../research.md#r4--coordinate-operations-and-response-delivery).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P14 — Login unguarded.** *Decided* (Q6). A stale login token fails its first guarded request, which a race test proves.
  **Decision:** Approved as written, 2026-09-24.
- [x] **P15 — Starting time limits.** *Decided* (Q4), for the spike to tune: shared wait 5 s, write 10 s, exclusive wait 15 s, deletion statement 30 s, export batches of 1,000 rows with a 120 s cap.
  **Decision:** Approved as written, 2026-09-24.

## Deletion and restore (R6)

- [x] **P16 — Deletion log lines.** *Decided* (Q2). `deletion.intent`, `deletion.committed` and `deletion.rolled_back` lines carrying the account UUID and boundary only, in the existing 30-day console logs. They still need the FR-004/FR-019 justification (Q2e).
  Source: [data-model.md → Deletion log lines](../data-model.md#deletion-log-lines--operational-log-records-not-ef-entity).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P17 — Restore runbook.** *Decided* (Q2). Disable API ingress; restore with `--preserve-under-name`; diff by UUID; fall back to gap-checked logs; suspend unknown outcomes; delete the preserved branch; reopen.
  Source: [operations.md → Restore contract](../contracts/operations.md#restore-contract--fail-closed).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P18 — JWT signing-key rotation on every restore.** This signs out every user after any restore, not only affected accounts. It is the consequence of keeping integer JWT subjects (R5).
  **Decision:** Approved, 2026-09-24, with the rationale in research R5 (ID reuse and revived revoked tokens; 30-minute token expiry keeps the cost small). Added: the runbook copies newer `PasswordHash`/`TokenVersion` from the preserved branch. In the fallback path this is a recorded and disclosed limitation.

## Frontend and UI

- [x] **P19 — Screens and entry points.** A "Privacy & account" entry on the cover, separate from workout navigation. Notice, account privacy, export and deletion screens, as in the current [UI preview](../../../docs/ui/README.md).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P20 — Notice gate.** "Open the notebook" and deep links show the current notice before any notebook fetch when it hasn't been acknowledged. Privacy, contact, export and delete stay reachable without acknowledgement. The gate is UI routing, not authorization.
  **Decision:** Approved as written, 2026-09-24.
- [x] **P21 — Invalidation handling.** On an observed 401: clear the token and notebook/draft state, abort pending requests, revoke export object URLs, notify other same-origin tabs, and revalidate on back/forward-cache restores.
  Source: [research R9](../research.md#r9--ui-state-and-errors).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P22 — New UI states from Q5.** Handle 403 `account_suspended` at login with the privacy contact, 503 retry copy, and a warning that a retry after signing in again can duplicate a write. Update the docs/ui specification with these.
  **Decision:** Approved as written, 2026-09-24.

## Code layout

- [x] **P23 — New files.**
  - Backend: `AccountLifecycle.cs` (filter and guards), `PrivacyEndpoints.cs` (Minimal API mapping) and `NotebookExport.cs`.
  - Frontend: `api/privacy.ts` plus the new screens.
  - No new projects, repository layers or single-implementation interfaces.
  Source: [plan.md → Source Code](../plan.md#source-code-repository-root).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P24 — Operating documents in `docs/privacy/`.** Notices, processing decision, suppliers, retention, restore runbook, rights requests and release checklist.
  Source: [operations.md → Maintained artifacts](../contracts/operations.md#maintained-artifacts).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P25 — Feature disabled in production until gates pass.** [plan.md → Phase 1](../plan.md#phase-1--design-and-delivery-boundaries) requires this because `main` deploys automatically, but it does not say how.
  **Resolved:** see [plan.md → Phase 1](../plan.md#phase-1--design-and-delivery-boundaries).
  **Decision:** Backend flag `PRIVACY_LIFECYCLE_ENABLED`, off unless exactly `true`. When off, the new routes are unmapped and the frontend infers that state from a 404. The flag covers the new routes and UI only; the migration and endpoint filter ship unflagged. 2026-09-24.

## Infrastructure (from Q1)

- [x] **P26 — Log Analytics retention in Bicep.** Pin the App\*, `Usage` and `AzureActivity` tables to 30 days, and evaluate `immediatePurgeDataOn30Days`.
  Source: [research R7 → Verified settings](../research.md#r7--retention-is-more-than-configuration-intent).
  **Decision:** Approved as written, 2026-09-24.
- [x] **P27 — Leftover westeurope environment.** `cae-gymnotebook-prod-weu` and `workspace-rggymnotebookproddLkl` hold no apps and no data. Remove them, as an owner action outside this feature's code.
  **Decision:** Removed 2026-09-24 with owner approval. There were no apps, jobs, locks or IaC references. The environment was deleted and the workspace was deleted with `--force`. The resource group now holds only the swedencentral resources.
- [x] **P28 — Google Fonts.** Either self-host Cormorant Garamond and Lora, which removes the third-party request, or keep them and cover the request in the FR-004 processing decision and the notice.
  **Decision:** Self-host in a separate PR before this feature, 2026-09-24: five `.woff2` files plus the OFL license in `frontend/public/fonts/`, `@font-face` rules, and the Google links removed. No npm dependency. See research R8.

## Amendment 2026-09-25 — optional-details consent (Q10)

The proposals made by the consent amendment (spec FR-029–FR-035). This is [plan.md → Open Design Questions](../plan.md#open-design-questions) item Q10, and it blocks tasks T085 onward. Same rules as above.

- [x] **P29 — Consent pair on User.** Add `OptionalDetailsConsentVersion` (bounded string, max 64) and `OptionalDetailsConsentedAt` to User. Both are null or both are set, enforced by a check constraint. Refusal and withdrawal are not recorded; they leave the pair null. No backfill: existing accounts start without consent. The pair is exported under `privacyRecords` and deleted with User.
  Source: [data-model.md → Optional-details consent](../data-model.md#optional-details-consent-amendment-2026-09-25).
  **Decision:** Approved as written, 2026-09-25.
- [x] **P30 — Consent statement as its own repository artifact.** A versioned statement in `docs/privacy/consent/` (an index plus one file per version), separate from the privacy notice, embedded and validated at startup like the notices. There is no announced-successor mechanism: changing what the statement covers needs its own spec change (FR-034).
  Source: [data-model.md → Consent statement version](../data-model.md#consent-statement-version--repository-artifact-not-ef-entity).
  **Decision:** Approved as written, 2026-09-25.
- [x] **P31 — Three routes and the term "optional details".** `GET /privacy/optional-details-statement` (public), `PUT /account/privacy/optional-details-consent` (grant; current version only, else 409 `consent_statement_changed`; idempotent) and `DELETE /account/privacy/optional-details-consent` (withdraw, and "Don't allow"; returns `clearedWorkouts`; idempotent; no password). `GET /account/privacy` gains an `optionalDetails` block. "Optional details" is the name used in URLs, code, columns and the export.
  Source: [contracts/api.md → Endpoints](../contracts/api.md#endpoints).
  **Decision:** Approved as written, 2026-09-25.
- [x] **P32 — Enforcement on workout writes.** Without consent, `POST /workouts` and `PATCH /workouts/{id}` carrying a non-empty title, location, notes or bodyweight get 403 `optional_details_consent_required`. The whole request is rejected, never partially applied. Null, empty or omitted details are always accepted. Exercise names are not covered.
  Source: [contracts/api.md → Optional-details enforcement](../contracts/api.md#optional-details-enforcement-on-existing-workout-routes).
  **Decision:** Approved as written, 2026-09-25.
- [x] **P33 — Enforcement only with the flag on.** An exception to "the flag covers new routes only": with the flag off, workout writes behave exactly as today, matching the owner-accepted interim risk. Tested in both states.
  Source: [plan.md → Production disablement](../plan.md#phase-1--design-and-delivery-boundaries).
  **Decision:** Approved as written, 2026-09-25.
- [x] **P34 — UI: consent screen, editor opt-in and transition question.** A new route `/account/privacy/optional-details` for grant and withdrawal, with a review step before withdrawal. The editor and workout detail hide the four fields behind one "Add title, location, notes and bodyweight" entry. The notebook gate asks the transition question after the notice, only when `transitionPending` is true.
  Source: [contracts/ui.md → Optional-details consent transitions](../contracts/ui.md#optional-details-consent-transitions).
  **Decision:** Approved as written, 2026-09-25.
- [x] **P35 — Transition clearing by operator SQL.** At the deadline, 30 days after the flag is switched on, the operator runs one documented SQL statement that clears the details of accounts without consent, records only the counts in the release checklist, and confirms zero remain. No background job, API or admin screen, following P3's precedent.
  Source: [data-model.md → Transition clearing](../data-model.md#transition-clearing--operator-step-not-ef-entity).
  **Decision:** Approved as written, 2026-09-25.

## Outcome

**Q9 completed 2026-09-24:** all items P1–P28 are approved; the ones with decisions are recorded in their source documents.

**Q10 completed 2026-09-25:** all items P29–P35 are approved as written.


When every item is ticked, or has a **Decision** that has been applied to the source documents, mark Q9 answered in plan.md and run `/speckit.tasks`, then `/speckit.analyze`.
