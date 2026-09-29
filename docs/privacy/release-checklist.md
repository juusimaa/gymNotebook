# Release checklist

Owner/reviewer: Jouni Uusimaa. Version: draft 2026-09-29. This is the evidence record for switching `PRIVACY_LIFECYCLE_ENABLED` on in production, required by [plan.md → Release Gates and Evidence Owners](../../specs/001-privacy-account-lifecycle/plan.md#release-gates-and-evidence-owners) and T078. A link to a document or a passing local test is evidence **only for that scope**. Record the tester/reviewer, UTC date, build/commit, environment, observed result and a restricted evidence reference for every completed check. Do not commit user data, credentials, raw logs or rights-request material here.

**Current decision: release blocked.** A read-only Container Apps check on 2026-09-28 returned `PRIVACY_LIFECYCLE_ENABLED=false`. The flag stays `false` until every applicable release gate below has dated evidence, zero unresolved findings for released processing, and the owner approves T084. Status terms: **Recorded** means a design decision is documented; **Open** means required evidence is absent or incomplete; **Blocked** means a known issue prevents release; **Passed** requires observed acceptance evidence and reviewer sign-off. A draft artifact does not change an Open gate to Passed.

## FR-021 exceptional-retention decision

| Review question | Owner | Review date | Evidence reference / finding | Status |
| --- | --- | --- | --- | --- |
| Confirm that no app-user information is retained beyond FR-019/FR-020 limits, or cite the specifically reviewed specification/notice amendment with scope, obligation/claim, access limit and end condition | Jouni Uusimaa | Pending | [Retention schedule](retention.md#rules-and-schedule-fr-018fr-021): no longer-than-limit app-user exception is **approved**, but absence of an exception is **not yet established**. The 2026-09-29 aggregate scan supports `AzureActivity`/`Usage` as metadata-only at this inspection; Provider-internal copy timing was accepted by the owner on 2026-09-29 as a residual unknown governed by each DPA ([agreement review](suppliers.md#t076-agreement-review-2026-09-29)); the owner-controlled limits still need T077's observed disposal. The Proton backup-window conflict was resolved on 2026-09-29: mailbox deletion, including Trash, moves to closure + 11 months ([rights-requests.md](rights-requests.md#close-and-dispose-of-the-case-q8)). | Open; T077 disposal observation remains; do not assert “none discovered” yet |

## Plan release gates

| Gate from plan.md | Evidence owner | Decision date | Evidence reference and remaining finding | Status |
| --- | --- | --- | --- | --- |
| Plan/schema/API/UI review and constitution adoption | Project owner | 2026-09-25 | [Plan Q9](../../specs/001-privacy-account-lifecycle/plan.md#open-design-questions) and [proposal review](../../specs/001-privacy-account-lifecycle/checklists/proposal-review.md); planning approval, not operational sign-off. | Recorded |
| Lawful basis, health data and consent decision | Controller with appropriate reviewer | 2026-09-28 (P1/P2/P5 signed); 2026-09-29 (T042 approved) | Owner signed P1 and P2 after confirming the [registration description](t043-notice-review.md#registration-service-description) live and matching the registration flow. P5 was signed and P7's rights-request basis recorded on 2026-09-28; P3 consent amendment was approved on 2026-09-29. P4 bases remain unsigned pending T075–T077, and P6 supplier findings remain. See the [processing decision](processing-decision.md). | Open |
| Controller/contact/authority and complete notice | Project owner/controller | 2026-09-28 (owner reports); 2026-09-29 (partial wording approval) | Public identity, mailbox receipt/reply and commitment to personal workday checks recorded in the [T043 draft](t043-notice-review.md). Owner approved five wording sections on 2026-09-29. Purposes, recipients, retention, final whole-notice review, supplier/retention reconciliation and version evidence remain open. | Open |
| Hosting/database/logs/network/fonts/support recipients | Operator | 2026-09-29 (partial live review) | [Supplier inventory](suppliers.md#t076-read-only-checks-2026-09-29) records current Neon access/recovery settings and scoped Azure log routing. Account agreements, transfers, support and deletion assistance remain unverified. T075–T076. | Open |
| Proposed retention limits including deletion lines | Operator | Pending | [Retention schedule](retention.md) records maxima and live settings; actual content, sinks, provider copies and disposal remain T075–T077. | Open |
| Restore safety and evidence mechanism | Operator | Pending | [Restore runbook](restore.md) exists. Isolated branch-diff and log-fallback exercise, including interruption and gap cases, remains T074. | Open |
| In-flight cancellation and concurrency | Author/reviewer | Pending | Research R4 Part A is recorded; deployed HTTP/proxy Part B and acceptance decision remain T053. | Open |
| Usability, correctness and performance | Project owner | Pending | SC-001–SC-008 table below; T080 local checks are recorded, while owner review and T081–T083 remain. | Open |

## Success criteria (SC-001–SC-008)

| Criterion and required outcome | Evidence owner | Review date | Evidence reference / boundary | Status |
| --- | --- | --- | --- | --- |
| SC-001: notice discoverability, wording and all acknowledgement/version cases | Project owner | Pending | Synthetic development notices remain; T043 final wording and [quickstart §1](../../specs/001-privacy-account-lifecycle/quickstart.md#1-notice-and-acknowledgement--sc-001-sc-004) walkthrough required. | Open |
| SC-002: reviewed decisions and every actual recipient, with zero lawful-basis/agreement/transfer/retention findings | Controller and operator | Pending | [Processing decision](processing-decision.md), [supplier inventory](suppliers.md), [retention schedule](retention.md); T042 approved, while T041, T043 and T075–T077 remain open. | Open |
| SC-003: complete 100,000-set export within 60 s, correct fields and no leakage under recorded normal load | Author/reviewer | Pending | Local export implementation/tests do not replace T053 proxy behavior or T081 deployed reference run. Record payload, connection, duration and memory. | Open |
| SC-004: owner mobile and keyboard walkthrough of notice/export/delete plus consent grant/withdraw, each under 3 min excluding download | Project owner | Pending | T082 must record device/browser, steps, durations, pass/fail and evidence for every flow/mode; quickstart §6. | Open |
| SC-005: deletion safety, concurrency/lost-response cases and deployed reference deletion within 60 s with control account intact | Author/reviewer and operator | Pending | T061 local implementation/tests are recorded in [tasks.md](../../specs/001-privacy-account-lifecycle/tasks.md); T081 deployed run and retained-log conditions (T075–T077) remain. | Open |
| SC-006: restore exposes zero deleted-account records and verifies export/backup/log/evidence expiry boundaries | Operator | Pending | T074 isolated restore and T077 observed disposal; [restore runbook](restore.md) is procedure, not exercise proof. | Open |
| SC-007: published artifacts owned/reviewed, and every rights practice case answered or validly extended within a calendar month | Controller/operator | Pending | [Rights procedure](rights-requests.md), T043 final notice and T083 synthetic practice evidence required. | Open |
| SC-008: optional-details consent, refusal, withdrawal and transition scenarios; zero unconsented details at deadline | Author/reviewer and operator | Pending | T085–T098 implementation exists; [quickstart §6a](../../specs/001-privacy-account-lifecycle/quickstart.md#6a-optional-details-consent--sc-008), T082 walkthrough and dated post-switch T097 clearing remain. | Open |

## Individual release tasks

| Task | Owner | Evidence date | Required reference / observed result | Status |
| --- | --- | --- | --- | --- |
| T042 — consent conclusion and reviewed amendment | Controller | 2026-09-29 (owner approval in conversation) | Owner approved [FR-029–FR-035](../../specs/001-privacy-account-lifecycle/spec.md#functional-requirements) as written, including separate consent and the existing-value transition. Article 6(1)(a) and 9(2)(a) choices are in the [processing decision](processing-decision.md). T085–T098 implementation is recorded in [tasks.md](../../specs/001-privacy-account-lifecycle/tasks.md); other release gates still block rollout. | Passed for T042 only |
| T043 — final notice and monitored contact | Project owner | 2026-09-28 (owner reports and registration approval); 2026-09-29 (candidate and partial wording approval) | [Owner review draft](t043-notice-review.md) and [unpublished JSON candidate](notice-candidate.json) are outside the served notice directory. Owner confirmed public identity, reported mailbox receipt/reply and workday monitoring, and approved the registration wording. On 2026-09-29 they approved five complete sections and purposes paragraphs 1, 2 and 4. Purposes paragraph 3, recipients, retention, T075–T077 evidence, final whole-notice approval and published version remain open. | Open |
| T053 — deployed streaming/proxy spike Part B | Author/reviewer | Pending | Disposable Azure environment, HTTP matrix, timings, decision and teardown in research R4. | Open |
| T074 — isolated restore | Operator | Pending | Separate Neon test project, primary/fallback/gap/intent/rollback/replay/pre-UUID outcomes and branch cleanup. | Open |
| T075 — stored log content and limiter source address | Operator | 2026-09-28 (console scan); 2026-09-29 (repeat live scan, limiter fix check, new-path baseline) | [Research R7/R10](../../specs/001-privacy-account-lifecycle/research.md#r7--retention-is-more-than-configuration-intent) records aggregate-only scans. Only 529 frontend HTTP access lines were found, all before 2026-09-24 03:09 UTC; the 15 old connection-URL lines had no later recurrence. System-log patterns found no selected identifiers/credentials, and no other workspace table with request events was populated. The old lines await age-out around 2026-10-23/24. The limiter's shared ingress bucket was fixed in PR #82: revision `--0000093` runs with `FORWARDED_HEADERS_ENABLED=true`, and the owner's controlled two-network check on 2026-09-29 gave 429 to the exhausted client and 401 to the other ([research R10](../../specs/001-privacy-account-lifecycle/research.md)). A 2026-09-29 13:30 UTC baseline found zero lines naming the new privacy routes or deletion events; UUID and consent-word matches were Data Protection key IDs and EF SQL/migration text. The routes have not run in production yet, so repeat the scan in the T081 environment and just before T084. Pattern scans cannot rule out every identifier or provider-internal sink. | Open; age-out, T081 scan and pre-T084 rescan remain |
| T076 — provider settings, agreements and permissions | Operator | 2026-09-29 (live and public follow-up) | [Supplier inventory](suppliers.md#t076-read-only-checks-2026-09-29) records repeat Neon checks: one admin, no listed API keys/invitations, one unprotected branch, zero snapshots and six-hour history. Azure still routes to the recorded workspace with zero diagnostic exports on inspected scopes; `AzureActivity` is empty and `Usage` contains only console/system volume metadata at this inspection. Public DNS/HTTP checks still show Cloudflare as DNS-only. The owner's GitHub deployment-identity access choice remains recorded. The [agreement review](suppliers.md#t076-agreement-review-2026-09-29) found processor DPAs incorporated for Azure (individual MCA), Neon (Free, MCSA; sample DPA v3 read) and Cloudflare (Free, Self-Serve Subscription Agreement), and the owner accepted the internal-copy timing as a residual unknown. The Proton 30-day backup conflict with Q8 was resolved on 2026-09-29 by deleting mailbox copies at closure + 11 months; the mailbox has web-only access. Proton's review is complete: the owner accepted its consumer terms for the free personal account on 2026-09-29. Remaining: the pre-release recheck of customer-side permissions and the comparison against the final notice (T043). | Open; pre-release recheck and notice comparison remain |
| T077 — observed retention/disposal | Operator | 2026-09-29 (settings and Neon window) | [Research R7](../../specs/001-privacy-account-lifecycle/research.md#r7--retention-is-more-than-configuration-intent): workspace 30 days with `immediatePurgeDataOn30Days: true`; only `AzureActivity`/`Usage` at 90/90; `AzureActivity` empty. Oldest row in every populated table is from 2026-09-23 (6.4 days), so no expiry is observable yet: first 30-day expiry around 2026-10-23/24, first `Usage` expiry around 2026-12-22. Neon refused a schema read 7 hours back (“before retention window … 6h0m0s”) and allowed one at 1 hour; `main` is still the only branch. No persistent export copies exist by design (FR-013). | Open; row-age observation after 2026-10-24 remains |
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
