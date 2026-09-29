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

**Status:** Approved by the owner on 2026-09-29, after both spike parts passed (see below). The locks and export cancellation change existing authenticated endpoints, so approval depended on the validation spike below: a local part (A) and a separately authorized deployed-proxy part (B), each with pre-agreed criteria. Q3–Q6 are decided below. Spike Part A passed on 2026-09-25 and Part B passed by its decision rule on 2026-09-29 (results below). The owner approved the Part B outcome, including the documented in-transit residual, on 2026-09-29; see [plan.md → Open Design Questions](plan.md#open-design-questions).

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

**Part B results (2026-09-29, 14:04–15:39 UTC): passed by the decision rule.** Run together with T081 in one disposable environment, then torn down the same day.

- **Amendments to the T009 conditions (owner, 2026-09-29, conversation):**
  - The shipped `POST /account/export` was tested instead of spike-only streaming endpoints. The `spike/` branch predates the export, and the real endpoint is what will ship. Its per-chunk delivery guard is the check the spike endpoint was meant to imitate. No spike image was built.
  - A separate, empty Neon project was used instead of a branch of production, because a branch would have copied real users' data. Every other condition held: separate resource group, synthetic data only, warm-up before timing, two-replica rows, same-day teardown.
- **Environment:** resource group `rg-gymnotebook-spike` (swedencentral), deployed from the unchanged `infra/main.bicep` with the production image `main-05c9824` and no custom domain. `PRIVACY_LIFECYCLE_ENABLED=true` was set on the disposable API only. Ingress `transport: Auto` as in production. Database: new Neon project `proud-firefly-36001263`, PostgreSQL 17, `aws-eu-central-1`, pooled endpoint, so the path is the same Sweden-to-Frankfurt path as production's. The schema came from the image's own `--migrate` step. Each synthetic account got the SC-003 reference notebook (1,000 workouts × 10 blocks × 10 sets) through the same SQL as `TwoHostGymNotebookFixture.SeedReferenceNotebookAsync`.
  - **Operator caution:** `infra/main.bicep` hard-codes resource names (`ca-gymnote-prod-58dd-api`, `log-gymnote-prod-58dd`, …); `environmentName` only sets a tag. The disposable copies therefore had production's names and were kept apart only by the resource group. Every command named `rg-gymnotebook-spike` explicitly, and the workspace's resource ID was checked before deletion. Production (`--0000103`) was checked before and after and was not touched.
- **Measured round-trip time:** API to Neon about **26–27 ms**, estimated as median `/health` (one query) minus median `/privacy/notice` (no database), 40 requests each, two runs, warm app. This confirms the Q4 estimate of 25–30 ms.
- **Client:** `curl --no-buffer` driven by a script that logged the time and cumulative bytes of every read. In deletion cells it sent `POST /account/delete` from a second connection once a byte threshold had arrived (2 MB fast, 1 MB slow). Slow clients used `--limit-rate 300K`.

| Replicas | HTTP | Client | Bytes received (of ~10.4 MB) | Bytes after deletion returned | Deletion | How the stream ended |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1.1 | fast | 2,746,720 | 0 | 200, 1.06 s | curl 18 “transfer closed with outstanding read data” |
| 1 | 2 | fast | 2,554,520 | 0 | 200, 0.79 s | curl 92 “stream not closed cleanly: INTERNAL_ERROR” |
| 1 | 1.1 | 300 KB/s | 3,035,020 | 1,776,761 | 200, 0.80 s | curl 18 |
| 1 | 2 | 300 KB/s | 2,554,520 | 1,296,264 | 200, 0.81 s | curl 92 |
| 2 | 1.1 | fast | 2,554,520 | 0 | 200, 1.06 s | curl 18 |
| 2 | 2 | fast | 2,652,644 | 0 | 200, 1.34 s | curl 92 |
| 2 | 1.1 | 300 KB/s | 2,720,621 | 1,438,739 | 200, 0.94 s | curl 18 |
| 2 | 2 | 300 KB/s | 2,523,421 | 1,275,300 | 200, 0.80 s | curl 92 |

- **Reading the table:** in every cell the client received 2.5–3.0 MB in total, whether it read fast or slowly. So the app stopped at roughly the same point, one or two chunks after the deletion was sent, and a slow client's extra bytes after the deletion returned were already in the ingress and TCP buffers before the commit. The app logged `Notebook export aborted: delivery guard Revoked.` once per deletion cell (8), and each deletion logged one `deletion.intent` and one `deletion.committed` line, with no `deletion.rolled_back`. No cell ended as a clean download. The 2-replica rows ran on a revision with exactly two replicas and 100% of traffic.
- **Slow clients without deletion:**
  - At 110 KB/s the whole export completed in 91.8 s (curl 0, full size), so ingress idle and request timeouts do not cut a legitimate slow export that finishes inside the app's 120 s cap.
  - At 60 and 80 KB/s the app ended the export with its **10 s per-write timeout** (`Notebook export aborted: write timeout.`), 24 and 40 s after the start in two timed reruns (the first 60 KB/s run, whose start time was not recorded, ended at the client after 128.7 s with 7.9 MB). The client then kept draining 2–2.6 MB of buffered bytes for another 20–35 s before seeing curl 92. The ingress applies backpressure in bursts, so once the path's buffer is full a single write can wait more than 10 s. Only exports larger than that buffer and slower than about 100 KB/s are affected; at those speeds the reference export could not finish within the 120 s cap anyway (it needs about 87 KB/s). The download fails visibly and can be retried, so this is recorded as a known limit, not a failure of the rule.
- **Headers through the ingress:** `content-type: application/json`, `cache-control: no-store`, `content-disposition: attachment; filename="gym-notebook-export.json"`; no content length, so the response streams.
- **Decision rule outcome: pass.** The app writes nothing after the deletion commits. The partial-delivery window is the path's buffer: **0 bytes for a client that keeps up, up to about 1.8 MB measured after a deletion for a 300 KB/s client, and up to about 2.6 MB after a server-side abort for a 60–80 KB/s client.** These bytes were handed to transport before the commit, go only to the authenticated account holder's own open request, and never complete the file: every truncation surfaced as a failed download (curl 18 or 92). This is the already-transmitted residual the rule allows. **Owner approval (2026-09-29, conversation):** the owner approved this outcome, including the in-transit residual of up to about 1.8 MB after a deletion and the slow-client write-timeout limit, and R4 as written.
- **Teardown (2026-09-29, 15:39 UTC):** the disposable workspace was force-deleted (no soft-deleted copy listed), then the resource group; `az group exists` returned false. The Neon test project was deleted, as was an empty PG 18 project created and deleted a few minutes earlier by mistake (production runs 17). Synthetic credentials and export copies were deleted from the operator's machine.

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

**Status:** Direction chosen by the owner on 2026-09-24: pre-restore diff with a log fallback (see Owner decision below). Sub-questions Q2a–Q2c were answered the same day. The owner approved the requirement that restores cannot revive deleted accounts and must fail closed. The isolated restore exercise (T074) ran on 2026-09-29 and passed with one code defect (T099) and three runbook corrections; approval of the full protocol awaits T099 and the owner's review of those results.

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

**Isolated restore exercise (T074, 2026-09-29, 15:57–17:25 UTC): passed, with one code defect and three runbook corrections.**

- **Environment:** disposable resource group `rg-gymnotebook-restore` (swedencentral) deployed from the unchanged `infra/main.bicep` with image `main-8538bd7`, `PRIVACY_LIFECYCLE_ENABLED=true` and the pooled Neon endpoint, like production. Database: new Neon project `soft-sunset-11658699` (PostgreSQL 17, `aws-eu-central-1`, six-hour history). Synthetic accounts only. The owner approved the plan the same day, including these deviations: the rotated test secret was set with `az containerapp secret set` (a command line), not a secret tool; D (registered after `T`) ran in the primary run rather than a separate one; and the intent-plus-rolled-back case was simulated (see the defect below). Everything was deleted the same day (see Teardown).
- **Pre-UUID target:** migrations were first applied only up to `AddWorkoutDomain`, and that instant was recorded as `T_pre`. `neon branches schema-diff main ^self@T_pre` showed no `privacy_account_id`, `sign_in_suspended_at` or consent columns there, so the runbook's rule rejects the target. The reconciliation SQL cannot run against it either, because its join column does not exist. `AccountIdentityTests.ApiSource_RemovesUserRows_OnlyInAccountDeletionCode` and `…_ScanPatterns_DetectUserRemoval` passed (the Q2d invariant).
- **Run 1, primary path (preserved-branch diff):** accounts A, B and control C at `T1`. After `T1`, B changed password, A deleted their account and D registered. Ingress was disabled (the Azure FQDN then returned the platform's 404), the two remaining database sessions were idle probe connections, and `neon branches restore main ^self@T1 --preserve-under-name …` succeeded. The restored `main` matched the `T1` fingerprint exactly: A back, B's old credentials, no D.
  - The comparison staged 3 preserved users and found exactly 1 account to re-delete (A). A session ended before `COMMIT` left the database unchanged. The committed run deleted A's 3 workouts, 2 exercises and user row and copied B's newer credentials (1 row). A replay in a fresh session changed 0 rows. D was not recreated.
  - With ingress closed: zero A rows, zero surviving users whose credentials differ from the preserved branch, zero orphaned blocks or sets, and C identical to its `T1` and pre-restore fingerprints. The users sequence had rewound to 3.
  - The JWT secret was rotated, every active revision restarted, and the preserved branch deleted (only `main` left, no snapshots) before ingress returned.
  - After reopening: B's new password 200, old password 401, pre-`T` token 401; A cannot sign in; C signs in and reads its 3 workouts. **E, registered after reopening, received user ID 4, D's old ID, and D's pre-restore token still got 401.** This shows the rotation closing the ID-reuse risk (P18).
- **Run 2, log fallback:** accounts r2-a, r2-r, r2-i and control r2-c at `T2`. After `T2`, r2-a and r2-i were deleted, and r2-r's deletion ran while another session held a row lock on its workouts. After isolation and restore, the preserved branch was declared unusable and not read.
  - **Coverage proof** (new method, now in restore.md): API console lines from `T2` to the isolation instant, in 30-second bins. 6 of 6 bins were populated, each with at least 6 lines and 2 probe heartbeats (`SELECT 1` from `/health`); one revision; 493 lines ingested after the isolation instant; maximum ingestion delay 5 s.
  - **Evidence:** 5 well-formed lines. r2-a and r2-i: intent plus committed, so re-delete. r2-r: intent only, so suspend. A session ended before `COMMIT` changed nothing; the committed run removed 2 accounts (4 workouts, 4 exercises); a replay matched 0. The suspension returned exactly one row, and a replay kept the original timestamp.
  - **After reopening:** r2-r's correct password got **403 `account_suspended`**, a wrong one 401, and its old token 401. r2-a and r2-i cannot sign in; r2-c and Run 1's B still can; the C and r2-c fingerprints are unchanged.
  - **Intent plus rolled back (simulated):** adding the `deletion.rolled_back` line the code should have written for r2-r changed its classification from suspend to untouched, and changed no other account.
- **Run 3, log gap:** accounts r3-g and control r3-c at `T3`. The environment's log routing was switched to `none` at 16:43:12, r3-g was deleted at 16:44:23, and routing was restored to the same workspace; that update took until 17:04:09. After isolation and restore, **33 of 48 bins between `T3` and isolation were empty, with no heartbeat, so the coverage proof failed and ingress stayed closed.** There were zero deletion lines for r3-g in that interval, so a log-only reconciliation would have revived it; the runbook's gap rule is what prevents that. In a real incident the primary path would be the way forward.
- **Code defect found (blocks release until fixed):** when the deletion's `DELETE` hits `lock_timeout` (PostgreSQL `55P03`), EF Core's retry execution strategy wraps the `PostgresException` in an `InvalidOperationException` ("likely due to a transient failure"). `AccountDeletion.RunAsync` catches only `DbException`, so the request ends as an unhandled **500** with **no `deletion.rolled_back` line**, although nothing was deleted. The contract requires 503 `temporarily_unavailable` with Retry-After. The restore evidence then shows an intent with no terminal line for a definite rollback, so a fallback suspends the account instead of leaving it alone. This fails safe, but it is wrong, and it probably covers every transient database failure inside the deletion. Tracked as T099.
- **Runbook corrections, made in restore.md:** (1) a preserved branch has no compute after a restore, so the runbook now adds a read-only one before reading it; (2) with ingress disabled no revision has a traffic weight, so the revisions to restart are selected by `active`; (3) the history table's column is `migration_id`, not `"MigrationId"`. Also recorded there: the coverage query and its pass rule, that a log-routing change can take about 20 minutes to apply and is itself a gap, and that the platform returns 404 while ingress is disabled.
- **Teardown (2026-09-29):** all three preserved branches were deleted during the runs; the workspace was force-deleted and the resource group deleted; the Neon project was deleted at 17:20:31 UTC; synthetic credentials and CSV transfer copies were deleted from the operator's machine. Production was not touched.

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
  - **T069 (2026-09-26):** a re-inspection found 14 tables at 90/90: `AzureActivity`, `Usage` and twelve App\* tables, including `AppGenAIContent`, which the list above did not name.
    - PR #69 pinned all 14 to 30/30 and set `immediatePurgeDataOn30Days: true` in `infra/modules/log-analytics.bicep`, stating `enableLogAccessUsingOnlyResourcePermissions: true` so the live value is kept. What-if (module only) showed no problem.
    - **The deploy failed:** Azure rejected `AzureActivity` and `Usage` with "minimum allowed value 90 days and above". The other operations succeeded, and the running revisions were untouched (`/health` 200).
    - **Live result after that deploy:** the twelve App\* tables are 30/30 on the Analytics plan with their Microsoft schema intact (`AppRequests`: 39 standard columns). The workspace features include `immediatePurgeDataOn30Days: true`, and `enableLogAccessUsingOnlyResourcePermissions` is still `true`. `AzureActivity` and `Usage` stay at 90/90.
    - **Accepted exception:** `AzureActivity` and `Usage` are removed from the template list and kept at Azure's enforced 90 days. Neither holds app-user data: `Usage` is billing metadata (ingested volume per table), and `AzureActivity` has 0 rows because nothing sends the Activity Log to this workspace. Even if it did, it would record Azure operator actions. Routing the Activity Log or any user-identifying data into either table needs a review first. The retention schedule (T071) and the supplier check (T076) list both tables as exceptions.
    - **Still unverified:** Microsoft documents the purge flag at workspace level only, so whether it also covers per-table 30-day overrides is unknown unless Azure's documentation or observed row ages (T077) show it.
- **Log content (from code, not from the data):**
  - The API has no explicit logging calls; `Microsoft.AspNetCore` is at Warning, and EF Core does not log parameter values (no `EnableSensitiveDataLogging`).
  - The frontend nginx uses its default access log to the console: remote address, user agent, path and time. Whether the remote address is the visitor's or the ingress's is unverified. **Owner decision (analysis C1, 2026-09-24):** turn the nginx access log off (`access_log off;`, keeping `error_log`) in an early standalone PR. Stored lines age out about 30–31 days after that deployment; verify this before enabling the feature, or purge if enabling sooner.
  - A scan of stored log text for IPs, usernames, tokens or connection strings was not performed and remains an operator task.
- **T075 partial content scan (read-only, 2026-09-28):** KQL pattern counts over every row of `ContainerAppConsoleLogs_CL` in `log-gymnote-prod-58dd`, reviewed as counts and masked line templates only. No matched values are copied here.
  - **Scope:** 56,967 rows from 2026-09-23 04:07 to 2026-09-28 11:37 UTC (api 40,264, frontend 16,711). The oldest row in every table with data is from 2026-09-23, so no row is yet old enough to show 30-day disposal (T077). `AzureActivity` has no rows.
  - **Frontend:** 529 nginx access lines (client address, request line, referrer, user agent, forwarded-for), all between 2026-09-23 05:21 and 2026-09-24 03:09 UTC. None are newer, which is consistent with PR #47's `access_log off;`. The newest should age out around 2026-10-24; T077 must observe it. The remaining IP-like matches are nginx startup notices whose kernel version looks like an address; they hold no addresses.
  - **API:** no IPv4/IPv6 addresses, JWTs or `Bearer` values. EF Core parameter values are redacted in all 9,635 logged parameter lists. The 540 "username" matches are SQL text, column names and Npgsql stack-frame parameter names, not values. Usernames were not compared against the database.
  - **Finding (blocking, security):** 15 API lines from 2026-09-23 04:07–04:33 UTC are unhandled startup exceptions whose message echoes the whole Neon connection URL, including the database owner role's password. Npgsql could not parse the URL-form connection string and quoted it in the exception. No later occurrences. Treat that password as exposed to anyone with workspace read access: rotate it, update the Container App secret, and then purge the rows or let them expire (about 2026-10-23). Any malformed connection string would log the same way, so consider a startup guard before the flag is enabled.
  - **Remediation (2026-09-28):** the leaked string was for role `neondb_owner` on endpoint `ep-old-leaf-b1d1i74e`, in a **separate Neon project**, not production (`gymnotebook_owner` on `ep-square-art-b2hssqud`). The owner reset `neondb_owner` in that project, which is still in use, and also reset `gymnotebook_owner` in production and updated the `NEON_CONNECTION_STRING` GitHub secret. The owner confirmed on 2026-09-28 that the second project holds no Gym Notebook data, so it is not a recipient or copy for this service. R7's Neon inspection above covers the production project only. A redeploy of the unchanged image (`main-15027f3…`) put the new value in the Container App secret. Revision `--0000085` is healthy: `/health` 200, a wrong login gets 401, and new API lines hold no auth failures, unhandled exceptions or connection-string-like text. The 15 old lines now hold a dead password; they were not purged and expire with the 30-day table retention (about 2026-10-23), which T077 observes. The API was unavailable from about 11:47 to 16:06 UTC while the new credential was put in place. The startup guard in `ConnectionStringValidation.cs` now rejects an unparseable connection string without echoing it.
  - **Not covered:** `ContainerAppSystemLogs_CL` (the scan was not run in this session; the operator still needs to do it), and the limiter's client address (R10).
- **T075 follow-up (read-only, 2026-09-29, 07:03 UTC):** queried `ContainerAppSystemLogs_CL` in the same workspace over the last 35 days. The 7,184 stored rows ran from 2026-09-23 04:07 to 2026-09-29 06:56 UTC. Aggregate-only KQL over `Log_s` and `RawData` returned zero rows matching IPv4 or IPv6 patterns, `@`, `Bearer`, JWT-like strings, connection-string markers (`postgresql://`, `Host=`, `Password=`, `ConnectionString`), `username`, account/workout/exercise/password/token/secret/authorization words, HTTP URLs or notebook paths. The longest concatenated text was 389 characters. No raw lines or matched values were returned. This pattern scan reduces the log-content concern but cannot prove that every possible identifier is absent. A separate aggregate query over `ContainerAppConsoleLogs_CL` found 529 frontend HTTP access lines, the newest at 2026-09-24 03:09:04 UTC, and zero after 03:10 UTC. Their expiry, the limiter's live client address (R10), other sinks and provider disposal remain unverified; T075/T077 stay open.
- **T075/T076 follow-up (read-only, 2026-09-29, 07:48–07:56 UTC):** queried the live production workspace with aggregate-only KQL; no raw log lines or matched values were returned or saved. A workspace-wide table count found only `ContainerAppConsoleLogs_CL` (64,230 rows), `ContainerAppSystemLogs_CL` (7,304) and `Usage` (246) populated. `AzureActivity` had zero rows. All 246 `Usage` records named only the two Container Apps log tables as `DataType`, consistent with volume metadata, not app-user events. The workspace and both populated app-log tables read 30/30 days with `immediatePurgeDataOn30Days: true`; `Usage` and `AzureActivity` read 90/90. This verifies current configuration and content categories, not expiry (T077).
  - The console scan through 07:52 UTC found 529 frontend HTTP access-shaped lines, 238 on 2026-09-23 and 291 on 2026-09-24; the latest was 2026-09-24 03:09:04 UTC. No such line appeared from 2026-09-25 through the query. Their original 30-day deadline has not arrived; observe their removal around 2026-10-24 in T075/T077. Later IP-shaped regex matches in frontend logs are not HTTP access-shaped and need not be treated as new access lines.
  - API logs contained exactly 15 `postgresql://` matches, all on 2026-09-23 and last at 04:33:02 UTC, consistent with the rotated-credential incident above. No later URL, `Bearer ` or JWT-shaped match appeared. Every `bodyweight` and `notes` word match in this scan also contained a SQL operation word; this classifies the word matches as SQL text, not a check of every possible value. EF sensitive-data logging is not enabled in source. A new aggregate system-log scan through 07:48 UTC found 7,300 rows and zero IP/email, connection/JWT, workout/exercise/bodyweight/notes/health markers in `Log_s` plus `RawData`. Pattern checks cannot prove that all personal or health data are absent.
  - The environment still routes app logs to this workspace. Diagnostic-export counts were zero for both apps, the managed environment, this workspace and the subscription Activity Log. Those inspected scopes have no configured diagnostic export; provider-internal logging and any uninspected scope remain unknown. Microsoft documents that Container Apps HTTP ingress logs can carry request paths and `XForwardedFor`, but no HTTP-log table had rows here and no diagnostic setting enabled it at the inspected environment. See [Microsoft's HTTP-log schema](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring#http-logs).
- **T075/T077 check (read-only, 2026-09-29, 13:30–13:40 UTC):** aggregate-only queries against the production workspace and Neon project; no raw log lines or matched values were returned or saved.
  - **Retention settings (T077):** the workspace still reads 30 days with `immediatePurgeDataOn30Days: true`. Only `AzureActivity` and `Usage` are not at 30/30; both read 90/90. A workspace-wide union over 120 days found three populated tables: `ContainerAppConsoleLogs_CL` (68,061 rows), `ContainerAppSystemLogs_CL` (7,838) and `Usage` (256). `AzureActivity` is still empty. The oldest row in each table is from 2026-09-23 (6.4 days old; `Usage` from 05:00 UTC), so no row is old enough yet to show a 30-day or 90-day limit being enforced. The first observable 30-day expiry is around 2026-10-23/24; the first `Usage` expiry is around 2026-12-22.
  - **Neon history window (T077):** `neon branches schema-diff main ^self@<timestamp>` compares the schema with a past point in the branch's own history without creating a branch. At 1 hour before the check it returned an empty diff; at 7 hours before, Neon refused with `timestamp is before retention window … retention_window:"6h0m0s"`. The project still reads `history_retention_seconds: 21600`, `aws-eu-central-1`, and the only branch is `main`. This shows that customer-side point-in-time access is refused outside the six-hour window; it says nothing about Neon's internal durability copies (accepted residual unknown, [suppliers.md](../../docs/privacy/suppliers.md#t076-agreement-review-2026-09-29)).
  - **Newly introduced paths (T075):** counted console lines naming the privacy routes (`/account`, `/account/export`, `/account/delete`, `/account/privacy`, `/account/privacy/optional-details`, `/privacy/notice`, `/privacy/optional-details-statement`), the `deletion.intent`/`deletion.committed`/`deletion.rolled_back` events, the deletion-failure, export-abort and guarded-write messages. All were zero in both apps and in `ContainerAppSystemLogs_CL`, as expected with the flag off, nginx access logging off and no request logging in the API. Two other patterns were classified by masked template (UUIDs, numbers and quoted literals replaced): 132 UUID-shaped strings are ASP.NET Core Data Protection key IDs in the startup warning “No XML encryptor configured”, not user identifiers; 29 consent-word matches are EF SQL column names (27 `SELECT` statements) and the two migration DDL statements from 2026-09-28. This is a baseline only: the new routes have not run in production, so their real log output is first observable in the T081 disposable environment, and the scan must be repeated there and just before T084.
  - **Still open:** the 529 old nginx access lines and 15 dead-credential lines await age-out (T075/T077), `Usage` has its 90-day exception, and pattern scans cannot prove every identifier absent.
- **T075 disposable-environment scan (2026-09-29, before teardown):** the new routes' first real log output, from the R4 Part B/T081 environment ([R4 → Part B results](#r4--coordinate-operations-and-response-delivery)), queried before teardown with aggregate-only KQL.
  - **Exact-value check:** 10,593 API and 49 frontend console rows were searched for every synthetic account's username, password and the last 24 characters of its token (12 accounts, all of which registered, signed in and exported; nine were then deleted): **zero rows matched**. There were also zero JWT-shaped strings, `Bearer`, connection-string markers or IPv4 addresses in the API rows. The single IPv4-shaped frontend match was the known nginx startup kernel string.
  - **No request paths:** zero rows named `/account…` or `/privacy…`; the API logs no request lines and nginx access logging is off.
  - **Line templates the feature added** (masked): `deletion.intent PrivacyAccountId=<uuid> DeletionBoundaryAt=<time>` and `deletion.committed …` (9 each, one per deletion), `Notebook export aborted: delivery guard Revoked.` (8) and `Notebook export aborted: write timeout.` (3). They carry the account UUID and a timestamp only, as specified for P4.
  - **Other lines:** EF Core logs each SQL command at Information with parameter values redacted (the exact-value check above confirms that); four Data Protection key warnings; one `fail:` line from EF's first migration on an empty database (`__EFMigrationsHistory` did not exist yet).
  - This covers the log content T075 needed for the new paths. What remains for T075 is the age-out of the old production nginx and dead-credential lines around 2026-10-23/24 and a last production rescan just before T084.
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
- **T075 follow-up (2026-09-29):** the live API app has external HTTP ingress, Single revision mode and `maxReplicas: 1`. Its environment-variable names do not include `ASPNETCORE_FORWARDEDHEADERS_ENABLED`; source has no `UseForwardedHeaders`, and the limiter still keys on `Connection.RemoteIpAddress`. [Microsoft's Container Apps .NET guide](https://learn.microsoft.com/en-us/azure/container-apps/dotnet-overview#define-x-forwarded-headers) says the app sees the ingress as its client unless forwarded information is used. This makes a shared ingress-address bucket the expected live behavior, but the address seen by a production request was not directly observed. Treat the per-visitor limit as **unverified and likely ineffective as specified**; record a separate trusted-proxy fix and verify it with two controlled client paths before sign-off. [Container Apps ingress](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview#http-headers) appends to a caller-supplied `X-Forwarded-For` and vouches only for the rightmost IP, so trusting the leftmost value or arbitrary proxies would permit spoofing. No rate-limit flood was sent to production during this review.
- **Fix and verification (2026-09-29):** [PR #82](https://github.com/juusimaa/gymNotebook/pull/82) added `UseForwardedHeaders` behind the fail-closed `FORWARDED_HEADERS_ENABLED` switch. It takes only the rightmost `X-Forwarded-For` entry (`ForwardLimit = 1`), so a caller-supplied prefix is ignored, and it clears the known-proxy lists because external ingress is the only route in (see [PLAN.md → Rate limiting](../../PLAN.md#rate-limiting)). Tests cover separate forwarded clients, a forged leftmost entry and the switch-off case. Read-only check after the deploy (run succeeded 09:07 UTC): revision `ca-gymnote-prod-58dd-api--0000093` runs image `main-dbc1947…` with `FORWARDED_HEADERS_ENABLED=true`. The owner then ran the controlled two-network check and reported that it behaved as expected. The client that exhausted the limit got 429, while a second client on a different network still got the ordinary 401. The per-visitor limit is now **verified at this inspection**. It still holds the address in memory only, and the address is never logged. A change to the ingress, a proxy in front of it (for example turning on Cloudflare's HTTP proxy) or a custom VNet reopens this finding.

**Alternatives considered:** Unthrottled verification fails FR-009; sleep-based race tests are unreliable; automated/source checks do not prove owner mobile/keyboard acceptance.

## Evidence still required

Technical research choices are resolved as draft proposals. Controller/contact/authority, legal review, supplier settings/contracts, strict disposal, implementation proof and owner walkthrough remain the explicitly unverified release gates in plan.md. Generated artifacts are not approvals.
