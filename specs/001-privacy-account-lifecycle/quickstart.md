# Validation Guide: Privacy and Account Lifecycle

Draft, 2026-09-24. These are validation instructions for the future implementation. New endpoints and privacy tests do not exist yet. This planning run does not claim the scenarios pass.

## Prerequisites and setup

- .NET 10 SDK, the Node/npm version used by the repository, Docker running and PostgreSQL 17 test containers. Follow current `README.md` for the gitignored `.env` and SDK/user-secrets setup; do not print secret values.
- Reviewed plan/schema/API/UI, reviewed migration, synthetic accounts A and B, and an isolated ledger test store. Never perform deletion/restore exercises against a real user's data.
- Local notice fixtures are clearly marked synthetic test content. Production publication needs actual approved controller/contact/authority, legal decisions and provider evidence per [operations contract](contracts/operations.md).
- Include a populated account, an empty account and a control account. Exercise non-ASCII text, every nullable field, local dates around midnight, unfinished workouts, warm-up/added-weight sets, repeated exercise blocks and unused exercises.
- Configure independent storage only when implemented using the documented development mechanism. The future implementation must add exact new configuration names/setup to README.md and `.env.example` without committing values. Do not invent runnable provider commands at planning time.

From repository root, start the existing application stack:

```sh
docker compose up --build
```

The existing Compose frontend is http://localhost:3000 and API is http://localhost:8080. For SDK development, follow README.md; the current documented API address is http://localhost:5217 and Vite is http://localhost:5173. Scalar/OpenAPI is available only in Development. Use the local UI or Scalar for password-bearing requests; avoid credentials in shell history or copied diagnostic output.

## Required checks after implementation

Run from repository root:

```sh
dotnet format backend/GymNotebook.sln --verify-no-changes
dotnet test backend/GymNotebook.sln
```

Run from `frontend/`:

```sh
npm run typecheck
npm run lint
npm run format:check
npm test
npm run build
```

Backend tests apply migrations to real PostgreSQL through GymNotebookFactory. Privacy tests belong in that existing project and run with the full suite; no hypothetical filter is presented as an existing test. Concurrency tests use explicit barriers and bounded timeouts, not sleep-based guesses. Supplement TestServer with real HTTP/proxy checks for delivery semantics.

## 1. Notice and acknowledgement — SC-001, SC-004

1. While signed out, open the sign-in/registration screen and reach the notice within two actions. Verify version/effective date and all FR-002/025 disclosures against the reviewed inventory. Returning to login must not create acknowledgement.
2. Sign in as a migrated account with null acknowledgement; try “Open the notebook” and direct workouts/progress/exercise/editor links. The current notice must appear before notebook fetching/rendering. Privacy/contact/export/delete stay reachable.
3. Leave without Continue; return and verify it appears again. Continue successfully; inspect the recorded version/time and absence of any consent record. Another session of the same account must not be prompted again for that version.
4. Announce a reviewed test revision with a future effective date, verify visibility before activation, activate it, and verify the gate repeats. A stale tab submitting the prior version receives 409 and reloads; repeating the current acknowledgement is idempotent.
5. Exercise offline/API failure/401 without accidentally acknowledging or displaying a private notebook. Confirm current wording matches the version recorded.

## 2. Complete export — SC-003

Use account A with every supported field and B with deliberately distinct canary values. Follow [API/export contract](contracts/api.md), not hand-assembled assumptions.

1. Submit incorrect current password: no file, inline actionable error and no mutation. Expired/revoked JWT: 401 and local invalidation, no file. Exceed verification limits: 429 and retry guidance; confirm login/invite/CORS controls remain intact.
2. Export A with correct password. Parse one valid JSON file. Compare all expected fields and values with a test-only database snapshot, including Unicode, decimal values, local dates, nulls, order and foreign keys. Require 100% required data, explicit empty arrays, field explanations and zero B/credential values. An unused exercise must appear.
3. Export an empty account: account data plus empty arrays and null acknowledgement, not an error. A newly acknowledged account includes its retained record.
4. Pause export between related table queries; change A's workout from another connection, then resume. File must represent one pre-change snapshot with valid relationships, not mixed versions. Snapshot time comes from the database boundary.
5. Abort the connection; verify no persistent server-side export copy. Retry with password gives a fresh complete snapshot without notebook changes. Token expiry/password change mid-download must stop delivery.
   Also inject a deletion rollback followed by independent-preparation cleanup outage: while that account's preparation remains unresolved/retained, export must fail safely; after reconciliation/cleanup it succeeds with all retained personal records covered.
6. Generate the synthetic reference dataset through a test fixture: exactly 1,000 workouts, 10 blocks/workout and 10 sets/block (100,000 sets). Record normal service load, resources, connection speed, payload size, peak memory and duration. Complete valid export must be available within 60 seconds; larger notebooks must not be silently truncated.

## 3. Deletion, sessions and failure — SC-005

1. Open deletion review as A. Verify categories, irreversibility, optional export, backup deadline and original-clock restricted log exception. Cancel: compare database/state before/after, unchanged. Incorrect password or false/missing confirmation: unchanged.
2. Open A in two sessions and keep B active. Delete A using correct password/confirmation. Success follows active commit and independent finalization. Assert zero A account/exercise/workout/block/set/acknowledgement rows and no export files. Only permitted minimal evidence/reviewed logs remain; B is byte/value-equivalent to its baseline.
3. Verify every old A JWT fails on all protected routes, initiating browser clears local state, other clients clear on observed invalidation, and returning/back-cache tabs cannot display private state before revalidation. Downloaded user-owned files are outside this assertion.
4. Inject a failure before deletion commit: all notebook data survives; no success or partial usable notebook. Inject ambiguous commit or independent-finalization failure: safe uncertain outcome, no false rollback/success, restore remains closed until reconciled.
5. Lose the successful HTTP response; retry using old JWT. Expect 401 and neutral session/contact explanation, never a fabricated completion message.
6. Re-register the same username. Verify different account UUID, no inherited records, and old suppression replay cannot remove it. Also exercise integer sequence rewind in isolated recovery with signing-credential rotation.
7. Delete the reference 100,000-set account within 60 seconds under recorded normal conditions. Record wait/commit/finalization durations separately to expose contention or provider latency.

## 4. Controlled concurrency and transport

Run two independent application hosts against the same real Postgres and ledger fixture. For every scenario test both orderings with barriers before authorization, lock acquisition, commit and response flush:

| Overlap with deletion | Required outcome |
| --- | --- |
| Workout create/PATCH/delete, bulk replace, append/update/delete set, exercise rename/merge | Earlier protected write may commit before deletion then be erased; later write is denied; no orphan, recreated account or B mutation |
| Login/password change | Fresh validation after coordination; no valid access to deleted identity; password-change-first invalidates old delete token |
| Notice acknowledgement and /auth/me | No recreated privacy record; no stale tracked User reuse or missing-user 500 |
| Snapshot export and ordinary personal-data response | No newly authorized personal bytes after successful deletion commit; unfinished stream aborts, buffers/snapshot release |
| Slow/blocked client, cancellation or lock timeout | Bounded waits, rollback where precommit, no false completion and no indefinite deletion starvation |

Repeat streamed tests through real Kestrel and the intended ingress/proxy, with response buffering/caching inspected. Observe the last bytes handed to transport and the deletion commit; bytes already delivered cannot be recalled. TestServer alone cannot prove proxy cancellation. If a proxy independently continues an unfinished file after deletion, this is a release failure requiring redesign, not an acceptable warning.

## 5. Retention and isolated restore — SC-002, SC-006, SC-007

1. Complete supplier/processing/retention evidence from [operations contract](contracts/operations.md). Record actual settings, region/role/agreement/transfer evidence and review owners. Verify Google Fonts and other real browser requests. Do not mark absent contracts/settings verified from repository declarations.
2. Use controlled clocks in tests for 24-hour export limits (if any persistent copy is introduced), 30-day backup/log and 31-day evidence boundaries. Test a log collected 20 days before deletion: at most 10 days remain. Non-justified logs are erased/anonymized on deletion. Copying/restoring/updating ledger state cannot move deadlines.
3. Verify strict Azure retention setting, table overrides/total retention, ingestion delay, extra sinks and existing-data transition. Verify every Neon branch/restore window/snapshot and manual/non-production copy. Verify receipt copies too, including storage version/soft-delete behavior. Retention automation needs observed disposal evidence, not just a configured timer.
4. Create a synthetic backup before A's deletion, then delete A. Restore that backup into isolated storage with public ingress disabled. Fetch independent ledger and authoritative completeness/commit evidence; reconcile, replay suppression and verify zero A records while B remains intact. Rotate signing credentials and only then demonstrate controlled reopening.
5. Repeat with missing/corrupt/incomplete ledger, unresolved PREPARED, unavailable transaction evidence and interruptions before/after each step. Access must stay closed. Replay twice safely. Destroy/make unusable over-age sources before evidence expiry. Verify incompatible pre-UUID backups cannot silently bypass suppression.
6. Exercise the monitored contact route without sending real personal data. Practice access/correction/deletion/restriction/objection/portability/complaint and broader security-record review. Record receipt, proportionate verification, calendar-month deadline, response or timely justified extension/refusal. Include end-of-month arithmetic and reviewed rights-register disposal.

## 6. Owner walkthrough and acceptance record — SC-004

The project owner performs notice, export and deletion on mobile and keyboard-only desktop. Each flow must be discoverable/completable without assistance in under three minutes excluding download time. Record date, device/browser, steps, duration, pass/fail and evidence for each of the six flow/mode combinations. Confirm Continue is not consent and deletion explains active removal, backup expiry and original-clock log retention. Fix failures and repeat affected flows.

Keep an evidence table with SC-001–007, tester/reviewer, date, build/commit, environment, observed result and evidence link. Label source review, automated tests, HTTP/proxy tests, provider evidence, restore exercise and owner walkthrough separately. No code test substitutes for legal/provider approval or human walkthrough.

## Completion boundary

Release only with reviewed operational artifacts, zero blocking findings for affected processing, all required checks and SC evidence, and aligned PLAN.md/README.md/docs/ui specification/prototype. Request approval before creating a PR under AGENTS.md. This guide and the plan do not authorize implementation or deployment.
