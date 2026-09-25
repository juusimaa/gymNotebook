# Research: Privacy and Account Lifecycle

Date: 2026-09-24. Draft technical decisions for review. Repository inspection and official documentation research; no live provider audit or final legal conclusion.

## R1 — Preserve current boundaries

**Decision:** Keep the established projects, Minimal APIs, EF/Npgsql, PostgreSQL, React/TypeScript and concrete helpers. Preserve username/password, invite registration, JWT `sub`/`tv`, ownership 404s and invalid-cursor 400s.

**Rationale:** `Program.cs`, `Data/AppDbContext.cs`, `PLAN.md` and the constitution establish these choices. Deleting User invalidates subsequent JWT checks but not previously authorized requests. `OnTokenValidated` uses `FindAsync`, which can leave a tracked User in the scoped context; later lifecycle checks must deliberately read fresh state. `/auth/me` must tolerate concurrent deletion instead of throwing from `SingleAsync`.

**Alternatives considered:** Identity migration, session database, repository layers and new UI packages add scope without meeting an unmet requirement.

## R2 — Notice and acknowledgement

**Decision:** Publish immutable versioned notice content from reviewed repository artifacts. Expose it through a public API and UI route. Store only the latest acknowledged version and timestamp on User, initially null. Maintain explicit current and pending versions; announce material revisions before activation, then switch the notebook gate to the activated version.

**Rationale:** FR-003/026 require preserved wording, cross-session suppression and no fabricated history. Continue accepts only the current version; stale tabs reload. Same-version repeats are idempotent. Privacy/contact/export/delete remain reachable before acknowledgement. Pre-announcement does not prove absent users have read it; any change requiring consent or different existing-data treatment needs the specified amendment first.

**Alternatives considered:** Browser-only state fails cross-session behavior; consent checkboxes contradict the spec; a full acknowledgement history retains more than required.

## R3 — Consistent export

**Decision:** Use one read-only PostgreSQL REPEATABLE READ transaction for all export queries. Capture `snapshotAt` with the database clock at the first snapshot query. Project only permitted fields, order deterministically, and stream one JSON document with embedded explanations. Enumerate large collections with readers or keyset batches inside that snapshot; do not build the entire graph in memory or call paginated public endpoints.

Under a short shared initialization guard on a separate READ COMMITTED connection, freshly verify password/JWT and establish the snapshot. Deletion evidence lives outside the database under R6, so export has no outstanding deletion records to reconcile first. Release that guard before enumeration; the long-lived snapshot transaction must never hold an account advisory lock.

**Rationale:** Separate READ COMMITTED queries can observe different states, whereas repeatable read supplies a stable view. Avoid persistent files and public URLs. Interrupted JSON is a failed download, never a completed export. Preserve unused exercises and repeated blocks. [PostgreSQL isolation](https://www.postgresql.org/docs/17/transaction-iso.html).

**Alternatives considered:** Ordinary API pagination mixes snapshots; CSV/ZIP contradict the chosen format; queued jobs/storage add unnecessary authorization and retention surfaces.

## R4 — Coordinate operations and response delivery

**Status:** Candidate design, not owner-approved. The locks and export cancellation change existing authenticated endpoints, so implementation waits on the validation spike below: a local part (A) and a separately authorized deployed-proxy part (B). Approval follows only if both pass their pre-agreed criteria. Q3–Q6 are decided below. Spike Part A passed on 2026-09-25 (results below); the remaining open item is Part B of the Q7 validation spike, tracked in [plan.md → Open Design Questions](plan.md#open-design-questions).

**Decision:** Use PostgreSQL transaction advisory locks in a dedicated account namespace keyed by existing User.Id. Ordinary authenticated operations acquire shared access and freshly check account identity, token version and expiry. Deletion and password changes acquire exclusive access. Login does not take the guard (Q6 below): a token issued in a race is rejected on first use by the per-request check. Existing explicit transactions join the lifecycle boundary rather than creating nested transactions.

The export snapshot is the explicit exception to a long-lived operation lock; its short initialization guard and separate delivery guards are defined in R3 and below. Deletion's terminal success response contains only its minimal outcome metadata and is authorized by the completed operation, not by rechecking a now-absent User. Password-change delivery checks the newly committed token version under a fresh shared guard after releasing its exclusive transaction; if deletion or another password change wins first, do not emit the stale token. Never reacquire a conflicting lock through another connection while still holding exclusive access.

For personal responses, hold shared access through a bounded write/flush and check fresh authorization before handing bytes to the response. Export uses separate short READ COMMITTED delivery transactions/connections between chunks, outside its snapshot. Release between chunks so deletion can acquire exclusive access; after deletion commits, the next check aborts delivery and disposes the snapshot. Do not attempt shared-to-exclusive lock upgrades. Bound waits and writes and propagate cancellation so a stalled client cannot block deletion indefinitely.

**Rationale:** Every relevant endpoint must participate, including old routes. Database-wide coordination covers multiple app processes. Transaction locks release automatically; session locks are unsuitable for this pooled-connection design. [PostgreSQL locking](https://www.postgresql.org/docs/17/explicit-locking.html), [EF transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

**Alternatives considered:** JWT checks alone leave in-flight races; locking for the whole download prevents deletion cancellation; rechecking inside the old snapshot sees stale state; local cancellation registries miss other instances/restarts.

**Mandatory proof:** Two app hosts, actual flush/cancellation and deployed proxy behavior. Bytes already handed to transport cannot be recalled. Disable response buffering/caching on this path and test slow downloads. If the real proxy continues unfinished delivery independently after deletion, revise the design before release. [ASP.NET HttpContext and Abort](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/use-http-context?view=aspnetcore-10.0).

**Q3 decision (owner, 2026-09-24): endpoint filter on the authorized route groups.**

- **Where:** a concrete filter in the proposed `AccountLifecycle.cs`, with no interface. It is attached to `/exercises`, `/workouts`, `/auth/me` and the new privacy routes that need only shared access. The filter takes shared access only.
- **What it does:**
  - It begins a transaction on the request's scoped `AppDbContext`. That is the same instance the handler and `OnTokenValidated` receive, so the handler's queries run inside it.
  - It takes `pg_advisory_xact_lock_shared(<lifecycle namespace>, userId)`, then freshly checks that the account exists and the token version matches, under the lock.
- **Reads:** the filter writes the result to the response while holding the lock, within the bounded write timeout, then commits.
- **Writes:** the filter commits first, then writes the response under a short **delivery guard**: a new transaction, a fresh shared lock and a fresh token check. This is the same pattern as password-change delivery above, so a 2xx is never sent for work that failed to commit. If deletion wins between commit and delivery, no personal response is written; the Q5 outcome applies.
- **Existing transactions:** the explicit `BeginTransactionAsync` calls in PUT `/workouts/{id}/exercises` and POST `/workouts/{id}/sets` are removed. Those handlers run inside the filter's transaction, and their intermediate `SaveChangesAsync` calls stay.
- **Export** does not use this filter. It uses its own initialization and delivery guards (R3 and above).
- **Exclusive endpoints own their transaction (analysis I1, owner decision 2026-09-24):** `/auth/change-password` and `POST /account/delete` do not use the filter. Wrapping them would need a shared-to-exclusive upgrade, which this section forbids and which deadlocks two concurrent upgraders.
  - Each endpoint takes exclusive access itself through a concrete helper in `AccountLifecycle.cs` (for example `AcquireExclusiveAsync`), using the Q4 exclusive wait (15 s).
  - Each owns its commit. Deletion needs this to write its post-commit log line, write `deletion.rolled_back` and map an uncertain commit to 503 `deletion_outcome_unknown`. Change-password needs it to deliver the new token under a fresh shared guard after commit.
  - An exclusive endpoint never holds the shared filter lock at the same time.
- **Coverage test:** a test enumerates the endpoint data source and fails if any endpoint that requires authorization lacks the filter, unless it is on an explicit allow-list: export, change-password and delete, each with a documented reason. The lock tests prove that each allow-listed endpoint takes its own guard.
- **`OnTokenValidated`:** its token-version check stays as an early rejection before any transaction or lock is taken.
- **Connection pooling:** each guarded request holds one pooled connection for its duration, including the response write, which is bounded by the write timeout. Transaction-level advisory locks remain valid through Neon's transaction-mode pooler. **Confirmed (owner, 2026-09-25):** the production connection string uses the `-pooler` endpoint. Consequence: per-transaction settings such as `lock_timeout` and the deletion `statement_timeout` must be set with `SET LOCAL` inside the transaction, because a session-level `SET` would leak to other clients sharing the pooled server connection. Session-level advisory locks remain ruled out.
- **Alternatives rejected:**
  - Middleware starts the response before commit, so it would need buffering or the same split, plus an export special case.
  - A per-handler helper cannot be verified from endpoint metadata.
  - Opening the transaction in `OnTokenValidated` has no commit hook.

**Q5 decision (owner, 2026-09-24): wait and revocation outcomes.**

- **Deletion cannot acquire exclusive access within its bounded wait:** 503 `temporarily_unavailable` with `Retry-After`. Nothing was mutated, so retry is safe (FR-017). By the same rule, an ordinary request whose shared-lock wait times out gets the same 503.
- **An ordinary request waited behind a deletion that committed:** the fresh check under the lock finds no account, so it gets 401, the same as a revoked token. No new status reveals the deletion.
- **A write committed but its delivery guard fails** (the account was deleted, or a password change revoked the token): 401 with no body. No personal bytes are sent after revocation.
  - Known consequence: after a password change the write did commit, so a user who signs in again and retries a POST can duplicate it. Document this in the UI copy for the re-sign-in state.
- **A suspended account signs in** (R6 Q2c): the password is verified first.
  - Wrong password: the existing 401.
  - Correct password: 403 `{ "code": "account_suspended" }`, and the UI shows the privacy contact path.

  Only someone who already knows the password learns the status. Existing tokens are invalid after restore because of the signing-key rotation, so token validation needs no new case.

**Q6 decision (owner, 2026-09-24): login takes no lifecycle guard.** Login keeps its current lookup, BCrypt verification and issuance, with no lock or transaction.

- A token issued while a deletion or password change commits is harmless. It carries the user ID (`sub`) and token version (`tv`), and every guarded request re-checks both, so the token gets 401 on first use.
- Integer IDs are not reused, except on a restore sequence rewind, which the JWT signing-key rotation covers (R5).
- The login response contains only the token, so no personal data is delivered to a deleted account.
- The accepted cost is feedback: in this rare race, login returns 200 and the next `/auth/me` returns 401, so the user is returned to sign-in without an explanation.
- A test must prove that a token issued in each race (deletion first, password change first) gets 401 on a guarded route.
- The suspended-account 403 (Q5) is a plain check after password verification. It needs no guard, because suspension is set only while ingress is disabled.

**Q4 starting values (owner, 2026-09-24).** These are starting points that spike Part A tunes; they are not final limits.

| Limit | Start | Basis |
| --- | --- | --- |
| Shared-lock wait, ordinary requests | 5 s, then 503 (Q5) | Per-transaction `lock_timeout`; ordinary requests wait only while a deletion holds exclusive access |
| Response write/flush timeout | 10 s per write or flush | Bounds how long a slow client holds shared access; an ordinary read's whole response write must fit |
| Deletion's exclusive-lock wait | 15 s, then 503 (Q5) | Longer than the longest shared hold (handler plus 10 s write). PostgreSQL queues later shared requests behind a waiting exclusive one, so deletion is not starved; ordinary requests arriving during that window may get 503 |
| Deletion transaction statement timeout | 30 s | 15 s wait + 30 s work stays within SC-005's 60 s |
| Export batch | 1,000 rows per keyset batch, flushed per batch, one delivery guard per chunk | The reference notebook is about 10 MB, or about 110 chunks. At about 3–4 round trips per guard, roughly 0.1 s cross-region, that is about 11 s total, within SC-003's 60 s. Deletion stops an export within one chunk |
| Export hard cap | 120 s, then abort the stream and dispose of the snapshot | 60 s is the target; the cap bounds the snapshot transaction |
| Spike A1 pass criterion | p95 increase ≤ 10 ms locally at 20 concurrent clients; connection pool never exhausted | Deployed overhead is reported from measured round-trip time, not used as pass/fail |

The cross-region figure is an estimate: the API runs in Azure swedencentral and Neon in aws-eu-central-1 (R7). Part B measures the actual round-trip time. Combining the lock acquisition and token-version check in one statement saves a round trip per guarded request. **Open (2026-09-25), settled by spike A6:** under READ COMMITTED a statement reads from a snapshot taken when it starts, so a single statement that waits on the lock while a deletion commits may still see the deleted user and pass the check. Two statements (lock, then check) avoid this; sending both in one `NpgsqlBatch` may keep the single round trip.

**Validation spike:** Time-boxed, on a separate `spike/` branch. Spike code is not merged into feature code. Part A tests are kept as the start of the permanent R4 suite.

**Implementation delegation (owner, 2026-09-25, constitution Principle III):** AI implements spike Part A (T008) in full, on the `spike/r4-cancellation` branch. The delegation covers only the spike: the prototype guards, the fake delete and export endpoints, the two-host fixture, the load harness and the A1–A6 tests. It does not cover T010–T027 or any other feature code. Spike code that is later kept, such as the fixture for T010 or tests for T012/T013, goes through ordinary owner review in the PR that adopts it. *Superseded the same day by constitution 2.0.0: AI now implements by default, so this scope limit no longer applies to T010–T027.*

- **Part A — local (no extra authorization).** Testcontainers Postgres with two app hosts sharing one database.
  - A1: ordinary endpoints under load with and without the shared guard. Pass when the p95 latency increase stays within the Q4 bound (≤ 10 ms locally at 20 concurrent clients) and the connection pool is never exhausted.
  - A2: deletion on one host during a slow export on the other. Pass when deletion acquires exclusive access within its bounded wait and the export aborts at its next chunk check.
  - A3: a client that stops reading. Pass when the bounded write/flush times out, the guard is released and deletion proceeds.
  - A4: password change racing deletion. Pass when no stale token is emitted, no deadlock occurs and no conflicting lock is reacquired while exclusive access is held.
  - A5: existing explicit transactions join the lifecycle boundary without nesting.
  - A6: a request that waited on the shared lock behind a committed deletion gets 401. Run it against a single-statement lock-and-check, two separate statements, and a two-statement `NpgsqlBatch`. Pass for a variant when it always returns 401; T019 uses a variant that passes.
- **Part B — deployed proxy (requires owner approval of Azure resources).** A disposable Container Apps environment from `infra/`, with the same ingress settings as the API app (`transport: 'auto'`), torn down afterwards.
  - A test-only endpoint streams synthetic data (never personal data) in N delayed chunks, checking a revocation flag under a short shared guard before each chunk. A second endpoint sets the flag under exclusive access.
  - A `curl --no-buffer` client logs per-chunk byte counts, timestamps and how the stream ended.
  - The matrix covers HTTP/1.1 vs HTTP/2, normal vs `--limit-rate` slow clients, and one vs two replicas.
  - Measure bytes received after the revocation commits, whether an upstream abort ever reaches the client as a clean end of stream, whether ingress buffers before first byte, and whether ingress idle/request timeouts cut legitimate slow exports.
  - **Approval (owner, 2026-09-25, T009): approved with conditions.**
    - Deploy into a separate, disposable resource group, never the production one.
    - Use a throwaway Neon branch as the database, never the production database.
    - Use synthetic data only.
    - The test-only endpoints exist only on the `spike/` branch and ship only in a spike-only image tag, never in a `main` or production image.
    - Warm the app up before measuring round-trip time, because the API scales to zero and a cold start would skew the numbers.
    - Tear down the resource group and the Neon branch the same day, and record the teardown with the results.
    - Keep the two-replica rows even though production currently runs `maxReplicas: 1`, so R4 does not need re-proving if the API later scales out.
- **Decision rule.** The final JSON closing bytes are written only after the final authorization check, so the proxy cannot complete an export the app did not finish. The open questions are the size of the partial-delivery window and whether truncation is always visible.
  - Pass: the window is bounded to about one chunk/ingress buffer, documented as the already-transmitted residual, and truncation always surfaces as a failed download. Approve R4 as written.
  - Fail: large buffering, or aborts delivered as clean completion. Revise R4 before implementation, for example with smaller chunks plus a client-verified end marker, or a non-streamed export.
- Record results, the ingress configuration tested and the date here before changing Status.

**Part A results (2026-09-25): passed.** Branch `spike/r4-cancellation` (commit `964204b`, not merged). Two app hosts on one Testcontainers PostgreSQL 17, run on a developer laptop. Streaming scenarios ran on real Kestrel over loopback, because TestServer has no socket buffers. The full spike suite (22 tests) passed on repeated runs, and the existing suite (115 tests) still passes with the spike switched off.

| Scenario | Result | Evidence |
| --- | --- | --- |
| A1: overhead | **Pass.** p95 increase at 20 concurrent clients: +0.7–1.5 ms for `GET /workouts` and +1.7–3.3 ms for `POST /workouts/{id}/sets` (which adds the delivery guard), against the ≤ 10 ms bound. No errors, and the pool (capped at 20) was never exhausted | 3 runs of 3,000 requests per host and operation. The batch variant was about 0.7–1 ms faster than two separate statements |
| A2: deletion during export | **Pass.** A deletion committed between chunks returned 200 within 15–24 ms. The export aborted at the next check ("before chunk 3"), no bytes reached the client after the commit, the stream never ended with the closing bytes, and the client saw an `IOException`. No transaction was left idle. A deletion arriving while a chunk guard was held queued behind it, then completed, and the export aborted at the following chunk | Barrier hooks plus `pg_locks` and `pg_stat_activity` checks |
| A3: client stops reading | **Pass.** A stalled export holds no lifecycle lock while blocked, so deletion completed in under 50 ms, and the write timeout then ended the export (1 s configured, fired at about 1.01 s). A stalled guarded read of a multi-MB response did hold shared access. Deletion waited and completed once the write timeout released it (1.5 s configured: completed at 1.55 s; the Q4 value of 10 s: completed at 10.05 s) | Kestrel's default `MinResponseDataRate` did not end the stalled write: with a 30 s write timeout, deletion reached its 15 s wait and got 503 |
| A4: password change vs deletion | **Pass.** No stale token was emitted. A deletion committing between the password change's commit and its delivery made the change return 401 without a token. 30 staggered concurrent rounds covered both orderings (25 password-first, 5 deletion-first): never both 200, no deadlock, no 5xx | Across two hosts |
| A5: existing transactions | **Pass**, with a required filter rule (below). Nested `BeginTransactionAsync` throws, which confirms T021. Both handlers work when joining the filter's transaction, and a mid-handler 400 still rolls back whole | Checked that the rollback test fails when the filter commits on a 400: the old blocks were lost |
| A6: lock-check variants | **Single statement fails**, as predicted. A request that waited behind a committed deletion passed the guard, then returned 500 on `/auth/me` and `200 []` on `/workouts` for the deleted account. **Two statements and the batch both return 401** | Deterministic: the test waits until the request is visibly queued in `pg_locks`, then commits the deletion |

**Findings that T019–T022 must carry:**

- **Lock check:** use two statements or a two-statement `NpgsqlBatch`, never a single statement (A6). The batch passes and is faster. Putting `SET LOCAL lock_timeout` into the same batch would save one more round trip; the spike did not test that.
- **Commit only on success:** the filter must commit only when the handler returns a 2xx, and let disposal roll back anything else (A5). `PUT /workouts/{id}/exercises` returns 400 after intermediate saves, and its own transaction used to roll that back.
- **Write timeout below the exclusive wait:** the app's write timeout is the only bound on a stalled read's shared hold (A3). It must stay below deletion's exclusive wait. Q4's 10 s and 15 s satisfy this, and the spike did not change any Q4 value.
- **Write cancellation:** cancelling a Kestrel write aborts the whole connection, so `RequestAborted` also fires. Code that needs to tell a write timeout from a client disconnect must check the timer's own token.
- **Password change:** run BCrypt verification and hashing before taking exclusive access, and re-check `token_version` under the lock. Read the user with `AsNoTracking()`, because `OnTokenValidated`'s `FindAsync` leaves a tracked, possibly stale `User` in the request's context.
- **Deployed overhead** (reported, not pass/fail): the guard adds about 3 round trips to a read with two statements, or about 2 with the batch. A write adds about double that, because of the delivery guard. At the estimated 25–30 ms cross-region round trip (Q4), that is roughly 50–90 ms per read and 100–180 ms per write. Part B's measured round-trip time replaces this estimate.

**Production smoke test (owner, 2026-09-25): passed.** The lifecycle foundation (T010–T027, PR #55) deployed to production from `main`. The owner then signed in, opened the cover (`/auth/me`), saved a set and changed a password, and everything worked. This confirms that the guard's `SET LOCAL lock_timeout`, the transaction-level advisory locks and the two-statement `NpgsqlBatch` check work through Neon's transaction-mode pooler (`-pooler` endpoint) for ordinary guarded requests and for the exclusive password-change path. **It is not Part B:** it did not test streamed responses through Container Apps ingress, the partial-delivery window during an export, or measured round-trip time. Those remain for T053, before export ships.

## R5 — Removal and identity

**Decision:** Add immutable account UUID `PrivacyAccountId` for restore suppression, keeping integer keys/JWT claims. Backfill genuine UUIDs, never acknowledgement. Under exclusive coordination and fresh password verification, delete workouts (cascade blocks/sets), then exercises, then User and its acknowledgement, in one transaction. Log the R6 intent line before commit and the committed line after it. No receipt table is kept (R6 Q2a).

**Rationale:** UUIDs distinguish accounts after username reuse or sequence rewind during restore. Rotate JWT signing credentials before restored service access resumes so old integer subjects cannot authenticate as newly allocated identities. Explicit delete ordering preserves the exercise FK's Restrict semantics.

**P18 decision (owner, 2026-09-24): rotate the JWT signing key on every restore.** A point-in-time restore rewinds the `users` ID sequence and each row's `TokenVersion`, which creates two concrete risks:

- **ID reuse:** an account created after the restore point `T` is lost, and the next registration receives its integer ID with `TokenVersion` 0. The lost account's old token (`sub`, `tv` 0) would then authenticate as the new person.
- **Revived revoked tokens:** a password change after `T` is undone, so the tokens it revoked validate again.

Rotation closes both in the primary and fallback paths, and needs no evidence. The cost is signing every user out. With 30-minute tokens (`Jwt__ExpiryMinutes`), that is small, and it happens during a restore that already involves downtime. The operator generates the new secret directly into the Container Apps secret; it is never logged, printed or committed. Alternatives rejected: advancing the sequence and bumping every `TokenVersion` has the same sign-out effect but needs the preserved branch; bumping only changed accounts is error-prone.

**Credentials changed after `T`:** a restore also reverts `PasswordHash`, so a password changed because of compromise would work again.

- When the preserved branch is usable, the runbook copies `PasswordHash` and `TokenVersion` from it for every account whose values differ.
- In the fallback path there is no source for the newer values. The limitation is recorded in `docs/privacy/restore.md` and disclosed in the notice's security information.

**Alternatives considered:** Soft deletion retains active credentials/content; username tombstones can remove a new account; integer-only suppression needs separately proven sequence high-water recovery.

## R6 — Independent restore evidence

**Status:** Direction chosen by the owner on 2026-09-24: pre-restore diff with a log fallback (see Owner decision below). Sub-questions Q2a–Q2c were answered the same day. The owner approved the requirement that restores cannot revive deleted accounts and must fail closed. Approval of the full protocol awaits answers to those questions, failure-handling proof and an isolated restore exercise.

**Superseded proposal (31-day Azure Blob ledger).** Kept as the fallback design (option 3 below) if longer-lived database copies are introduced. Not the current direction.

Use a private Azure Blob ledger outside notebook restores. Durably write a minimal PREPARED receipt before committing database deletion; mark COMMITTED after database commit and before reporting success. The database transaction also writes a short-lived commit receipt. Object metadata/namespaces carry protocol state; personal payload contains only account UUID, deletion-boundary timestamp and absolute suppression expiry.

Definitive rollback allows removing the prepared record. Ambiguous commit/crash leaves PREPARED; reconcile with authoritative transaction evidence. Never treat missing evidence as rollback, nor a prepared intent as permission to erase an intact account. Restoration stays closed if evidence cannot resolve ambiguity. Postcommit ledger failure returns an uncertain outcome/contact path, not a claim of rollback. An invalid old JWT retry never certifies deletion.

**Rationale:** PostgreSQL and Blob writes are not atomic together. Explicit uncertainty with fail-closed recovery avoids pretending otherwise. The prepared record prevents a failed postcommit archive write from silently hiding a deletion.

Purge local commit receipts immediately after external finalization and in all cases before 24 hours from the deletion boundary. With independently verified backup lifetime of at most 30 days after a receipt could enter a copy, its residual copies expire before day 31. External evidence expires by day 31 from the original boundary. Unresolvable evidence requires disabling and destroying/making unusable affected restore sources before evidence expiry, not indefinite retention. Include versions, snapshots, soft deletion, diagnostic records and backups in disposal proof. Schedule cleanup ahead of deadlines.

**Alternatives considered:** Same-database tombstones disappear on rollback to an old backup; periodic exports have coverage gaps; uncoordinated dual writes have ambiguous failure; distributed transaction infrastructure is disproportionate.

**Sizing after Q1:** Q1 (R7 → Verified settings) found a 6-hour Neon history window, no snapshots, no snapshot schedule and no other controlled database copies. A deleted account can therefore be revived only by a restore to a point before its deletion, performed within 6 hours of that deletion. The 31-day ledger above was sized for backup copies that do not currently exist. Options:

1. **Restore rule using existing logs.** A restore target must be later than the most recent deletion. After commit, deletion writes a minimal line (account UUID and deletion-boundary timestamp only) to the existing 30-day Container Apps logs, and the restore procedure reads it before reopening. No new infrastructure. Weaker: a lost log line allows revival, and the line is subject to the R7 log-retention rules.
2. **Short-lived independent record.** Keep the PREPARED/COMMITTED protocol, but expire evidence at the restore window plus a margin (hours, not 31 days). This removes most expiry scheduling and the day-31 disposal proof, but still adds storage and a scale-to-zero reconciliation runner.
3. **Full ledger as proposed.** Justified only if longer-lived copies are planned, for example a paid Neon plan with longer history or snapshots.

Whichever option is chosen, any change to Neon plan, history retention, snapshots or branches must re-trigger this sizing check. That makes those settings a drift-checked release gate.

**Owner decision (2026-09-24): pre-restore diff with a log fallback.** A fourth option, chosen over options 1–3. It uses the restore mechanism itself as the evidence and needs no new storage.

- **Primary evidence: the pre-restore branch.** Restore with `neon branches restore main main@<T> --preserve-under-name <name>`, which keeps the pre-restore state as a separate branch. User rows disappear only through account deletion. So every `PrivacyAccountId` present in the restored database but absent from the preserved branch was deleted after `T`. Before any access resumes, delete those accounts again in the restored database. Then verify that none remain and delete the preserved branch. Accounts created and deleted after `T` exist in neither, and need nothing. UUIDs, not usernames, make the comparison safe across username reuse (R5).
- **Fallback evidence: minimal deletion log lines.** For restores where the preserved branch is unusable (the current database is lost or corrupt), deletion writes minimal structured lines to the existing 30-day Container Apps console logs. They carry only the account UUID and deletion-boundary timestamp, with no username or notebook content. The restore procedure re-deletes every logged UUID committed after `T`.
- **Fail closed.** If neither source can be verified for the interval from `T` to the restore, access stays closed, as already approved.
- **Unchanged:** isolated restore with public ingress disabled, JWT signing-key rotation before access resumes (R5), and original retention deadlines (R7).
- **Removed:** the Azure Blob ledger, PREPARED/COMMITTED protocol, independent finalization before the success response, 31-day evidence expiry and scale-to-zero cleanup runner. This answers Q2's original three questions by removing their subjects: completeness is proven by the database diff, and no new store needs cleanup.

**Owner answers to the sub-questions (2026-09-24):**

- **Q2a — DeletionReceipt removed.** The User row's absence in the preserved branch and the fallback log lines cover its purpose. This also removes its 24-hour purge.
- **Q2b — intent and committed lines, verified for gaps.** Under exclusive coordination, deletion logs an intent line (UUID and boundary) before commit, and a committed line after commit but before the success response. On definitive rollback it logs a rolled-back line, so the fallback can release that intent instead of suspending the account. The fallback counts as verified only if log ingestion shows no gap over the interval from `T` to the restore. Otherwise the restore stays closed.
- **Q2c — suspend sign-in on unknown outcome.** An intent line with no committed line means the outcome is unknown. The account is neither erased nor revived. Its sign-in is suspended until the owner resolves it through the privacy contact path, and the rest of the restore may reopen. This needs a suspension marker on User, which is a new schema proposal subject to Q9 review.

**Consequences to document (not choices):**

- **Q2d — diff invariant.** Account deletion must remain the only way a User row disappears. Document this as an invariant and guard it with a test.
- **Q2e — log line as personal data.** The pseudonymous account UUID in 30-day logs must appear in the FR-004 processing decision and the FR-019 log-retention justification.

**Capability boundary (applies to the superseded ledger, option 3):** Asynchronous storage lifecycle policy alone is not proof of a precise disposal deadline. [Azure Blob lifecycle behavior](https://learn.microsoft.com/en-us/azure/storage/blobs/lifecycle-management-policy-structure). The new infrastructure, access policy, identity integration and receipt protocol require review and crash-point tests. Existing provider settings were inspected under Q1 (R7), but no ledger resource exists yet, so its disposal behavior is unverified.

## R7 — Retention is more than configuration intent

**Decision:** Keep the spec maxima and original absolute deadlines; verify settings and disposal for every copy. Reflect configuration in IaC or a repeatable deployment step. Keep restored service isolated until source-age, deletion coverage and reconciliation checks pass.

For this design, identifying external logs may be collected only where continued retention after account deletion has a reviewed necessity/basis. Otherwise anonymize before collection or remove that collection. Clear incompatible existing identifying logs before enabling deletion. Do not rely on asynchronous external purge as part of the atomic database delete; supporting non-justified identifying logs would require an explicitly designed and verified deletion-time disposal protocol before success.

**Rationale:** `infra/modules/log-analytics.bicep` declares 30-day workspace retention. Microsoft documents that this can retain 31 days without `immediatePurgeDataOn30Days`; table overrides and total retention also need inspection. Use the supported API in deployment if Bicep cannot express the strict setting. [Azure retention configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-retention-configure).

Neon restoration and branch capabilities are not this project's settings or verified disposal guarantees. Inventory branches, snapshots, manual dumps, replicas and non-production copies; copying does not restart deadlines. [Neon restore capability](https://neon.com/blog/announcing-point-in-time-restore), [Neon branch workflow](https://neon.com/docs/get-started-with-neon/workflow-primer).

**Verified settings (read-only inspection, 2026-09-24):**

- **Neon project `dawn-pine-04463679`** (free plan, `aws-eu-central-1`):
  - `history_retention_seconds` 21600 (6 hours).
  - No snapshots and no automatic snapshot schedule.
  - One branch (`main`).
  - No `pg_dump`/backup scripts in the repo, CI or infra.
  - Neon's internal durability copies are not visible from the project and need supplier evidence (R8).
- **Azure `rg-gymnotebook-prod`:**
  - Active Container Apps environment `cae-gymnote-prod-58dd` with workspace `log-gymnote-prod-58dd` (swedencentral).
  - A leftover environment `cae-gymnotebook-prod-weu` with workspace `workspace-rggymnotebookproddLkl` (westeurope, created 2026-09-22, no apps, no log rows in 90 days). Removed with owner approval on 2026-09-24 (P27): the environment was deleted, and the workspace was deleted with `--force`, so no soft-delete copy remains.
  - No storage accounts, backup vaults or resource diagnostic settings.
- **Log Analytics retention:**
  - Both workspaces keep 30 days, and `ContainerAppConsoleLogs_CL` is 30/30.
  - `immediatePurgeDataOn30Days` is not set.
  - The App\*, `Usage` and `AzureActivity` tables are at 90 days. They currently hold no user data (no Application Insights; `Usage` is billing metadata), but should be pinned to 30 days in IaC.
  - Only `ContainerAppConsoleLogs_CL`, `ContainerAppSystemLogs_CL` and `Usage` contain rows.
- **Log content (from code, not from the data):**
  - The API has no explicit logging calls; `Microsoft.AspNetCore` is at Warning, and EF Core does not log parameter values (no `EnableSensitiveDataLogging`).
  - The frontend nginx uses its default access log to the console: remote address, user agent, path and time. Whether the remote address is the visitor's or the ingress's is unverified. **Owner decision (analysis C1, 2026-09-24):** turn the nginx access log off (`access_log off;`, keeping `error_log`) in an early standalone PR. Stored lines age out about 30–31 days after that deployment; verify this before enabling the feature, or purge if enabling sooner.
  - A scan of stored log text for IPs, usernames, tokens or connection strings was not performed and remains an operator task.
- **Third-party requests:** `frontend/index.html` loads Google Fonts from `fonts.googleapis.com`/`fonts.gstatic.com`, so visitors' IP addresses reach Google. This belongs in the FR-004 processing decision; self-hosting the fonts would remove it.

**Alternatives considered:** A provider name or Bicep default is insufficient evidence; row deletion does not remove backup history; silently extending the specification's limits is out of scope.

## R8 — Legal decision and recipients

**Decision:** Produce a purpose-by-purpose decision with reviewer/date, necessity, rationale, possible health-data classification and consent conclusion. Unknown lawful basis, additional condition, agreement, transfer or retention evidence blocks the relevant rollout. A consent finding requires the specified amendment before implementing that flow.

**Rationale:** A notice is not consent and a contract rationale alone does not settle health-data treatment. This plan selects no legal basis and certifies no compliance. [GDPR official text](https://eur-lex.europa.eu/eli/reg/2016/679/), [EDPB consent guidance](https://www.edpb.europa.eu/documents/guideline/guidelines-052020-on-consent-under-regulation-2016679_en).

`frontend/index.html` requests Google Fonts CSS/font resources. Azure and Neon are repository-supported candidates; DNS/CDN and support recipients require discovery.

**P28 decision (owner, 2026-09-24): self-host the fonts in a separate PR before this feature.**

- **What:** serve the five existing styles (Cormorant Garamond 400/600; Lora 400, 500, italic 400) as Latin-subset `.woff2` files from `frontend/public/fonts/`, with `@font-face` rules in the existing styles and the three Google `<link>` tags removed from `frontend/index.html`. The typefaces stay the same.
- **Licensing:** both typefaces are under the SIL Open Font License 1.1. Ship the license text next to the files.
- **No dependency:** no npm package is added; downloaded files meet the need.
- **Effect:** once merged and verified in real browser traffic, Google is no longer a recipient. It drops out of the supplier inventory, the notice and the FR-004 review.
- **Until then**, Google Fonts remains a recipient candidate in the operations inventory.

**Alternatives considered:** Blanket acceptance contradicts FR-006; assuming all fitness data is or is not health data skips review; template controller/contact values cannot be published.

## R9 — UI state and errors

**Decision:** Reuse `api/client.ts`, route guards, token storage and current UI preview. Add authenticated download/AbortSignal support. Distinguish invalid session (401) from a wrong password on new operations (400, `password_verification_failed`). Observed invalidation clears token, notebook/draft state, pending requests and export object URLs; notify same-origin tabs. Revalidate returning tabs and back/forward-cache restores before showing private state.

**Rationale:** Current 401 handling is primarily in the route loader; an active screen can otherwise retain data until navigation. Login's existing generic 401 remains unchanged for a wrong username or password. The one addition is the password-first 403 `account_suspended` for a suspended account (R4, Q5). Never store passwords in URLs, logs or browser persistence. Offline devices and downloaded files cannot be remotely erased.

**Alternatives considered:** Treating any password failure as deletion harms retry; treating any 401 as deletion success violates FR-017; adding a cookie/analytics banner invents behavior.

## R10 — Throttling and validation

**Decision:** Apply the existing per-IP auth limiter to new password operations plus a per-account sensitive-operation bucket, proposed 10 attempts/60 seconds with no queue. Bound concurrent exports per account. Reuse ASP.NET primitives for the declared single-replica topology; verify live routing and concurrent revisions. Multiple serving instances require a shared limiter before claiming that aggregate bound.

**Rationale:** Existing counters are in-process. Account keys come only from validated identities; untrusted forwarding headers cannot define IP identity. Use deterministic synchronization/time controls and independent test fixtures. [ASP.NET rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

**P12 decision (owner, 2026-09-24):**

- **One export per account:** enforced by `pg_try_advisory_xact_lock(<export namespace>, userId)`, taken by the export's snapshot transaction. If it is already held, the request gets an immediate 429.
  - It is correct across instances and revision overlaps, and PostgreSQL releases it on commit, abort or a lost connection.
  - The namespace is separate from the R4 lifecycle namespace, and deletion never takes it. So it does not violate R3's rule against holding the lifecycle lock on the snapshot.
- **Per-account password throttle (10 attempts per 60 s):** stays in process, like the existing auth limiter. Its documented bound is the limit times the number of running instances: normally one, and two during a Single-mode revision overlap. A shared limiter is needed before claiming an exact aggregate bound with more replicas.
- **Finding (unverified, predates this feature):** `Program.cs` has no forwarded-headers configuration. Behind Container Apps ingress, `Connection.RemoteIpAddress` is probably the ingress's internal address, so the existing per-IP `auth` limiter may be one shared bucket for all visitors. Verify the deployed remote address separately. If confirmed, fix it outside this feature with trusted proxy configuration, never by trusting arbitrary `X-Forwarded-For`. The new operations inherit whatever the per-IP policy does.

**Alternatives considered:** Unthrottled verification fails FR-009; sleep-based race tests are unreliable; automated/source checks do not prove owner mobile/keyboard acceptance.

## Evidence still required

Technical research choices are resolved as draft proposals. Controller/contact/authority, legal review, supplier settings/contracts, strict disposal, implementation proof and owner walkthrough remain the explicitly unverified release gates in plan.md. Generated artifacts are not approvals.
