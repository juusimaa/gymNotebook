# Release checklist

Owner/reviewer: Jouni Uusimaa. Version: draft 2026-09-28. This is the evidence record for switching `PRIVACY_LIFECYCLE_ENABLED` on in production, required by [plan.md → Release Gates and Evidence Owners](../../specs/001-privacy-account-lifecycle/plan.md#release-gates-and-evidence-owners) and T078. A link to a document or a passing local test is evidence **only for that scope**. Record the tester/reviewer, UTC date, build/commit, environment, observed result and a restricted evidence reference for every completed check. Do not commit user data, credentials, raw logs or rights-request material here.

**Current decision: release blocked.** A read-only Container Apps check on 2026-09-28 returned `PRIVACY_LIFECYCLE_ENABLED=false`. The flag stays `false` until every applicable release gate below has dated evidence, zero unresolved findings for released processing, and the owner approves T084. Status terms: **Recorded** means a design decision is documented; **Open** means required evidence is absent or incomplete; **Blocked** means a known issue prevents release; **Passed** requires observed acceptance evidence and reviewer sign-off. A draft artifact does not change an Open gate to Passed.

## FR-021 exceptional-retention decision

| Review question | Owner | Review date | Evidence reference / finding | Status |
| --- | --- | --- | --- | --- |
| Confirm that no app-user information is retained beyond FR-019/FR-020 limits, or cite the specifically reviewed specification/notice amendment with scope, obligation/claim, access limit and end condition | Jouni Uusimaa | Pending | [Retention schedule](retention.md#rules-and-schedule-fr-018fr-021): no longer-than-limit app-user exception is **approved**, but absence of an exception is **not yet established**. Neon internal copies, Azure actual disposal and Proton mailbox copies need T076–T077. `AzureActivity`/`Usage` at 90 days are metadata-only candidates pending T076 content/routing confirmation. | Open; do not assert “none discovered” yet |

## Plan release gates

| Gate from plan.md | Evidence owner | Decision date | Evidence reference and remaining finding | Status |
| --- | --- | --- | --- | --- |
| Plan/schema/API/UI review and constitution adoption | Project owner | 2026-09-25 | [Plan Q9](../../specs/001-privacy-account-lifecycle/plan.md#open-design-questions) and [proposal review](../../specs/001-privacy-account-lifecycle/checklists/proposal-review.md); planning approval, not operational sign-off. | Recorded |
| Lawful basis, health data and consent decision | Controller with appropriate reviewer | 2026-09-28 (provisional P1/P2 choice) | Owner provisionally chose Article 6(1)(b) for essential P1/P2 processing and approved the [registration wording and placement](t043-notice-review.md#registration-service-description). Owner concluded on 2026-09-28 that P2 is not health data in this context (no Article 9 condition or consent). Publication and confirmation against the registration flow remain. P3 optional-details consent is recorded; P1/P2/P4/P5 final decisions and T042 sign-off remain. | Open |
| Controller/contact/authority and complete notice | Project owner/controller | 2026-09-28 (owner reports) | Public identity, mailbox receipt/reply and commitment to personal workday checks recorded in the [T043 draft](t043-notice-review.md). Reviewed production wording, final purpose decisions, supplier/retention reconciliation and version evidence remain open. | Open |
| Hosting/database/logs/network/fonts/support recipients | Operator | Pending | [Supplier inventory](suppliers.md) records current configuration and unknown agreements, transfers, support and deletion assistance. T075–T076. | Open |
| Proposed retention limits including deletion lines | Operator | Pending | [Retention schedule](retention.md) records maxima and live settings; actual content, sinks, provider copies and disposal remain T075–T077. | Open |
| Restore safety and evidence mechanism | Operator | Pending | [Restore runbook](restore.md) exists. Isolated branch-diff and log-fallback exercise, including interruption and gap cases, remains T074. | Open |
| In-flight cancellation and concurrency | Author/reviewer | Pending | Research R4 Part A is recorded; deployed HTTP/proxy Part B and acceptance decision remain T053. | Open |
| Usability, correctness and performance | Project owner | Pending | SC-001–SC-008 table below; T080 local checks are recorded, while owner review and T081–T083 remain. | Open |

## Success criteria (SC-001–SC-008)

| Criterion and required outcome | Evidence owner | Review date | Evidence reference / boundary | Status |
| --- | --- | --- | --- | --- |
| SC-001: notice discoverability, wording and all acknowledgement/version cases | Project owner | Pending | Synthetic development notices remain; T043 final wording and [quickstart §1](../../specs/001-privacy-account-lifecycle/quickstart.md#1-notice-and-acknowledgement--sc-001-sc-004) walkthrough required. | Open |
| SC-002: reviewed decisions and every actual recipient, with zero lawful-basis/agreement/transfer/retention findings | Controller and operator | Pending | [Processing decision](processing-decision.md), [supplier inventory](suppliers.md), [retention schedule](retention.md); T042–T043, T075–T077 open. | Open |
| SC-003: complete 100,000-set export within 60 s, correct fields and no leakage under recorded normal load | Author/reviewer | Pending | Local export implementation/tests do not replace T053 proxy behavior or T081 deployed reference run. Record payload, connection, duration and memory. | Open |
| SC-004: owner mobile and keyboard walkthrough of notice/export/delete plus consent grant/withdraw, each under 3 min excluding download | Project owner | Pending | T082 must record device/browser, steps, durations, pass/fail and evidence for every flow/mode; quickstart §6. | Open |
| SC-005: deletion safety, concurrency/lost-response cases and deployed reference deletion within 60 s with control account intact | Author/reviewer and operator | Pending | T061 local implementation/tests are recorded in [tasks.md](../../specs/001-privacy-account-lifecycle/tasks.md); T081 deployed run and retained-log conditions (T075–T077) remain. | Open |
| SC-006: restore exposes zero deleted-account records and verifies export/backup/log/evidence expiry boundaries | Operator | Pending | T074 isolated restore and T077 observed disposal; [restore runbook](restore.md) is procedure, not exercise proof. | Open |
| SC-007: published artifacts owned/reviewed, and every rights practice case answered or validly extended within a calendar month | Controller/operator | Pending | [Rights procedure](rights-requests.md), T043 final notice and T083 synthetic practice evidence required. | Open |
| SC-008: optional-details consent, refusal, withdrawal and transition scenarios; zero unconsented details at deadline | Author/reviewer and operator | Pending | T085–T098 implementation exists; [quickstart §6a](../../specs/001-privacy-account-lifecycle/quickstart.md#6a-optional-details-consent--sc-008), T082 walkthrough and dated post-switch T097 clearing remain. | Open |

## Individual release tasks

| Task | Owner | Evidence date | Required reference / observed result | Status |
| --- | --- | --- | --- | --- |
| T042 — consent conclusion and reviewed amendment | Controller | 2026-09-28 (Article 6 choice) | Owner chose Article 6(1)(a) consent for the four optional details, alongside Article 9(2)(a); recorded in the [processing decision](processing-decision.md). P1/P2/P4/P5 decisions remain unsigned; reconcile FR-029–FR-035 approval with production processing. | Open |
| T043 — final notice and monitored contact | Project owner | 2026-09-28 (owner reports and registration approval) | [Owner review draft](t043-notice-review.md) prepared 2026-09-28. Owner confirmed public identity, reported Gmail receipt and a working reply, committed to checking the mailbox each working day including while away, and approved the registration wording and placement. Other bases, T075–T077 evidence, final notice approval and published version remain open. | Open |
| T053 — deployed streaming/proxy spike Part B | Author/reviewer | Pending | Disposable Azure environment, HTTP matrix, timings, decision and teardown in research R4. | Open |
| T074 — isolated restore | Operator | Pending | Separate Neon test project, primary/fallback/gap/intent/rollback/replay/pre-UUID outcomes and branch cleanup. | Open |
| T075 — stored log content and limiter source address | Operator | Pending | Restricted log scan, nginx age-out/purge and client-address observation in research R7/R10. | Open |
| T076 — provider settings, agreements and permissions | Operator | Pending | [Suppliers](suppliers.md) account-specific reviews, Neon internal copies/branch permissions, Azure 90-day metadata confirmation. | Open |
| T077 — observed retention/disposal | Operator | Pending | Strict Azure 30-day and 90-day table checks, Neon history expiry and all copy deadlines. | Open |
| T079–T080 — documentation and full required suites | Author/reviewer | 2026-09-28 | [Local validation record](#local-validation-t079t080): README/PLAN updated; all required suites and the focused export fixture passed. Owner review remains. | Open; local checks passed |
| T081 — deployed export and deletion reference | Operator | Pending | Disposable cross-region path, connection, duration, payload, memory, zero deleted and unchanged control account. | Open |
| T082 — owner walkthrough | Project owner | Pending | Mobile and keyboard flow/mode matrix, steps, timing and result. | Open |
| T083 — rights-request practice | Project owner | Pending | Synthetic cases, receipt/deadline calculation, response or valid extension, including end-of-month and no-sign-in cases. | Open |
| T084 — flag-on deployment | Project owner | Pending | Final gate review, reviewed PR, deployment/revision verification and exact UTC switch time. | Blocked by open gates |

## Local validation (T079–T080)

Run 2026-09-28 on local macOS arm64, branch `docs/t079-t080-privacy-validation` based on `f9690d8`. .NET SDK 10.0.100, Node v26.10.0, Docker Desktop with Testcontainers PostgreSQL 17. Evidence scope: local source, build and test execution only; no Azure, Neon cross-region, proxy, provider or owner walkthrough claim. Reviewer sign-off is pending.

| Command | Observed result |
| --- | --- |
| `dotnet format backend/GymNotebook.sln --verify-no-changes` | Passed, no formatting changes. |
| `dotnet test backend/GymNotebook.sln` | Passed: 250/250, none skipped. Includes local export and deletion reference tests. |
| `npm run typecheck` | Passed. |
| `npm run lint` | Passed. |
| `npm run format:check` | Passed. |
| `npm test` | Passed: 142/142 tests in 12 files. |
| `npm run build` | Passed. |
| `dotnet test backend/GymNotebook.sln --filter FullyQualifiedName~ExportPerformanceTests --logger 'console;verbosity=detailed'` | Passed: 1/1 over local Kestrel; 0.7 s export, 9.7 MiB payload. The reported 94 MiB allocated and 229 MiB working set are for the combined test process, not server peak memory. |

## Release decision

| Decision | Owner | Date | Evidence reference | Status |
| --- | --- | --- | --- | --- |
| Authorize production flag-on only after all applicable gates and SC checks pass, all released-processing findings are closed, and the owner has reviewed this record | Jouni Uusimaa | Pending | No authorization recorded. | Blocked |

## Dated steps after the flag switch

| Step | Due (UTC date) | Owner | Done on | Evidence | Status |
| --- | --- | --- | --- | --- | --- |
| Optional-details transition clearing, [retention.md → Optional-details transition clearing](retention.md#optional-details-transition-clearing-fr-035), steps 1 and 2 | T084 flag-on date + 30 calendar days: fill in when T084 is done | Operator | | `accounts_cleared` = , `workouts_cleared` = , follow-up count = (must be 0) | Not due |
