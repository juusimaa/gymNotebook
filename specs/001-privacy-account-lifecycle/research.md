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

Before starting, reconcile any retained PREPARED records for this account; fail export safely if their outcome/cleanup cannot be verified. Otherwise a still-live account could have a feature-created personal record omitted from its export. Under a short shared initialization guard on a separate READ COMMITTED connection, freshly verify password/JWT, confirm no outstanding preparation, and establish the snapshot. Release that guard before enumeration; the long-lived snapshot transaction must never hold an account advisory lock.

**Rationale:** Separate READ COMMITTED queries can observe different states, whereas repeatable read supplies a stable view. Avoid persistent files and public URLs. Interrupted JSON is a failed download, never a completed export. Preserve unused exercises and repeated blocks. [PostgreSQL isolation](https://www.postgresql.org/docs/17/transaction-iso.html).

**Alternatives considered:** Ordinary API pagination mixes snapshots; CSV/ZIP contradict the chosen format; queued jobs/storage add unnecessary authorization and retention surfaces.

## R4 — Coordinate operations and response delivery

**Status:** Candidate design, not owner-approved. The locks and export cancellation change existing authenticated endpoints, so implementation waits on the validation spike below: a local part (A) and a separately authorized deployed-proxy part (B). Approval follows only if both pass their pre-agreed criteria. Open design questions for this decision are tracked as Q3–Q7 in [plan.md → Open Design Questions](plan.md#open-design-questions).

**Decision:** Use PostgreSQL transaction advisory locks in a dedicated account namespace keyed by existing User.Id. Ordinary authenticated operations acquire shared access and freshly check account identity, token version and expiry. Deletion and password changes acquire exclusive access. Login revalidates its resolved account before token issuance. Existing explicit transactions join the lifecycle boundary rather than creating nested transactions.

The export snapshot is the explicit exception to a long-lived operation lock; its short initialization guard and separate delivery guards are defined in R3 and below. Deletion's terminal success response contains only its minimal outcome metadata and is authorized by the completed operation, not by rechecking a now-absent User. Password-change delivery checks the newly committed token version under a fresh shared guard after releasing its exclusive transaction; if deletion or another password change wins first, do not emit the stale token. Never reacquire a conflicting lock through another connection while still holding exclusive access.

For personal responses, hold shared access through a bounded write/flush and check fresh authorization before handing bytes to the response. Export uses separate short READ COMMITTED delivery transactions/connections between chunks, outside its snapshot. Release between chunks so deletion can acquire exclusive access; after deletion commits, the next check aborts delivery and disposes the snapshot. Do not attempt shared-to-exclusive lock upgrades. Bound waits and writes and propagate cancellation so a stalled client cannot block deletion indefinitely.

**Rationale:** Every relevant endpoint must participate, including old routes. Database-wide coordination covers multiple app processes. Transaction locks release automatically; session locks are unsuitable for this pooled-connection design. [PostgreSQL locking](https://www.postgresql.org/docs/17/explicit-locking.html), [EF transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

**Alternatives considered:** JWT checks alone leave in-flight races; locking for the whole download prevents deletion cancellation; rechecking inside the old snapshot sees stale state; local cancellation registries miss other instances/restarts.

**Mandatory proof:** Two app hosts, actual flush/cancellation and deployed proxy behavior. Bytes already handed to transport cannot be recalled. Disable response buffering/caching on this path and test slow downloads. If the real proxy continues unfinished delivery independently after deletion, revise the design before release. [ASP.NET HttpContext and Abort](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/use-http-context?view=aspnetcore-10.0).

**Validation spike:** Time-boxed, on a separate `spike/` branch. Spike code is not merged into feature code. Part A tests are kept as the start of the permanent R4 suite.

- **Part A — local (no extra authorization).** Testcontainers Postgres with two app hosts sharing one database.
  - A1: ordinary endpoints under load with and without the shared guard. Pass when p95 latency increase stays within an agreed bound and the connection pool is never exhausted.
  - A2: deletion on one host during a slow export on the other. Pass when deletion acquires exclusive access within its bounded wait and the export aborts at its next chunk check.
  - A3: a client that stops reading. Pass when the bounded write/flush times out, the guard is released and deletion proceeds.
  - A4: password change racing deletion. Pass when no stale token is emitted, no deadlock occurs and no conflicting lock is reacquired while exclusive access is held.
  - A5: existing explicit transactions join the lifecycle boundary without nesting.
- **Part B — deployed proxy (requires owner approval of Azure resources).** A disposable Container Apps environment from `infra/`, with the same ingress settings as the API app (`transport: 'auto'`), torn down afterwards.
  - A test-only endpoint streams synthetic data (never personal data) in N delayed chunks, checking a revocation flag under a short shared guard before each chunk. A second endpoint sets the flag under exclusive access.
  - A `curl --no-buffer` client logs per-chunk byte counts, timestamps and how the stream ended.
  - The matrix covers HTTP/1.1 vs HTTP/2, normal vs `--limit-rate` slow clients, and one vs two replicas.
  - Measure bytes received after the revocation commits, whether an upstream abort ever reaches the client as a clean end of stream, whether ingress buffers before first byte, and whether ingress idle/request timeouts cut legitimate slow exports.
- **Decision rule.** The final JSON closing bytes are written only after the final authorization check, so the proxy cannot complete an export the app did not finish. The open questions are the size of the partial-delivery window and whether truncation is always visible.
  - Pass: the window is bounded to about one chunk/ingress buffer, documented as the already-transmitted residual, and truncation always surfaces as a failed download. Approve R4 as written.
  - Fail: large buffering, or aborts delivered as clean completion. Revise R4 before implementation, for example with smaller chunks plus a client-verified end marker, or a non-streamed export.
- Record results, the ingress configuration tested and the date here before changing Status.

## R5 — Removal and identity

**Decision:** Add immutable account UUID `PrivacyAccountId` for restore suppression, keeping integer keys/JWT claims. Backfill genuine UUIDs, never acknowledgement. Under exclusive coordination and fresh password verification, delete workouts (cascade blocks/sets), then exercises, then User and its acknowledgement; insert a minimal commit receipt in the same transaction.

**Rationale:** UUIDs distinguish accounts after username reuse or sequence rewind during restore. Rotate JWT signing credentials before restored service access resumes so old integer subjects cannot authenticate as newly allocated identities. Explicit delete ordering preserves the exercise FK's Restrict semantics.

**Alternatives considered:** Soft deletion retains active credentials/content; username tombstones can remove a new account; integer-only suppression needs separately proven sequence high-water recovery.

## R6 — Independent restore evidence

**Status:** Candidate design, not owner-approved. The owner approved the requirement that restores cannot revive deleted accounts and must fail closed; approval of this protocol awaits provider-capability evidence, failure-handling proof and an isolated restore exercise.

**Proposal:** Use a private Azure Blob ledger outside notebook restores. Durably write a minimal PREPARED receipt before committing database deletion; mark COMMITTED after database commit and before reporting success. The database transaction also writes a short-lived commit receipt. Object metadata/namespaces carry protocol state; personal payload contains only account UUID, deletion-boundary timestamp and absolute suppression expiry.

Definitive rollback allows removing the prepared record. Ambiguous commit/crash leaves PREPARED; reconcile with authoritative transaction evidence. Never treat missing evidence as rollback, nor a prepared intent as permission to erase an intact account. Restoration stays closed if evidence cannot resolve ambiguity. Postcommit ledger failure returns an uncertain outcome/contact path, not a claim of rollback. An invalid old JWT retry never certifies deletion.

**Rationale:** PostgreSQL and Blob writes are not atomic together. Explicit uncertainty with fail-closed recovery avoids pretending otherwise. The prepared record prevents a failed postcommit archive write from silently hiding a deletion.

Purge local commit receipts immediately after external finalization and in all cases before 24 hours from the deletion boundary. With independently verified backup lifetime of at most 30 days after a receipt could enter a copy, its residual copies expire before day 31. External evidence expires by day 31 from the original boundary. Unresolvable evidence requires disabling and destroying/making unusable affected restore sources before evidence expiry, not indefinite retention. Include versions, snapshots, soft deletion, diagnostic records and backups in disposal proof. Schedule cleanup ahead of deadlines.

**Alternatives considered:** Same-database tombstones disappear on rollback to an old backup; periodic exports have coverage gaps; uncoordinated dual writes have ambiguous failure; distributed transaction infrastructure is disproportionate.

**Capability boundary:** Asynchronous storage lifecycle policy alone is not proof of a precise disposal deadline. [Azure Blob lifecycle behavior](https://learn.microsoft.com/en-us/azure/storage/blobs/lifecycle-management-policy-structure). The new infrastructure, access policy, identity integration and receipt protocol require review and crash-point tests; no resource or provider setting was verified.

## R7 — Retention is more than configuration intent

**Decision:** Keep the spec maxima and original absolute deadlines; verify settings and disposal for every copy. Reflect configuration in IaC or a repeatable deployment step. Keep restored service isolated until source-age, deletion coverage and reconciliation checks pass.

For this design, identifying external logs may be collected only where continued retention after account deletion has a reviewed necessity/basis. Otherwise anonymize before collection or remove that collection. Clear incompatible existing identifying logs before enabling deletion. Do not rely on asynchronous external purge as part of the atomic database delete; supporting non-justified identifying logs would require an explicitly designed and verified deletion-time disposal protocol before success.

**Rationale:** `infra/modules/log-analytics.bicep` declares 30-day workspace retention. Microsoft documents that this can retain 31 days without `immediatePurgeDataOn30Days`; table overrides and total retention also need inspection. Use the supported API in deployment if Bicep cannot express the strict setting. [Azure retention configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-retention-configure).

Neon restoration and branch capabilities are not this project's settings or verified disposal guarantees. Inventory branches, snapshots, manual dumps, replicas and non-production copies; copying does not restart deadlines. [Neon restore capability](https://neon.com/blog/announcing-point-in-time-restore), [Neon branch workflow](https://neon.com/docs/get-started-with-neon/workflow-primer).

**Alternatives considered:** A provider name or Bicep default is insufficient evidence; row deletion does not remove backup history; silently extending the specification's limits is out of scope.

## R8 — Legal decision and recipients

**Decision:** Produce a purpose-by-purpose decision with reviewer/date, necessity, rationale, possible health-data classification and consent conclusion. Unknown lawful basis, additional condition, agreement, transfer or retention evidence blocks the relevant rollout. A consent finding requires the specified amendment before implementing that flow.

**Rationale:** A notice is not consent and a contract rationale alone does not settle health-data treatment. This plan selects no legal basis and certifies no compliance. [GDPR official text](https://eur-lex.europa.eu/eli/reg/2016/679/), [EDPB consent guidance](https://www.edpb.europa.eu/documents/guideline/guidelines-052020-on-consent-under-regulation-2016679_en).

`frontend/index.html` requests Google Fonts CSS/font resources. Include Google as an external-resource recipient candidate and verify actual browser traffic. Azure and Neon are repository-supported candidates; DNS/CDN and support recipients require discovery. Preserve the existing typefaces; self-hosting would require reviewed licensing and implementation, not an implicit change in this plan.

**Alternatives considered:** Blanket acceptance contradicts FR-006; assuming all fitness data is or is not health data skips review; template controller/contact values cannot be published.

## R9 — UI state and errors

**Decision:** Reuse `api/client.ts`, route guards, token storage and current UI preview. Add authenticated download/AbortSignal support. Distinguish invalid session (401) from a wrong password on new operations (400, `password_verification_failed`). Observed invalidation clears token, notebook/draft state, pending requests and export object URLs; notify same-origin tabs. Revalidate returning tabs and back/forward-cache restores before showing private state.

**Rationale:** Current 401 handling is primarily in the route loader; an active screen can otherwise retain data until navigation. Login's existing generic 401 remains unchanged. Never store passwords in URLs, logs or browser persistence. Offline devices and downloaded files cannot be remotely erased.

**Alternatives considered:** Treating any password failure as deletion harms retry; treating any 401 as deletion success violates FR-017; adding a cookie/analytics banner invents behavior.

## R10 — Throttling and validation

**Decision:** Apply the existing per-IP auth limiter to new password operations plus a per-account sensitive-operation bucket, proposed 10 attempts/60 seconds with no queue. Bound concurrent exports per account. Reuse ASP.NET primitives for the declared single-replica topology; verify live routing and concurrent revisions. Multiple serving instances require a shared limiter before claiming that aggregate bound.

**Rationale:** Existing counters are in-process. Account keys come only from validated identities; untrusted forwarding headers cannot define IP identity. Use deterministic synchronization/time controls and independent test fixtures. [ASP.NET rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

**Alternatives considered:** Unthrottled verification fails FR-009; sleep-based race tests are unreliable; automated/source checks do not prove owner mobile/keyboard acceptance.

## Evidence still required

Technical research choices are resolved as draft proposals. Controller/contact/authority, legal review, supplier settings/contracts, strict disposal, implementation proof and owner walkthrough remain the explicitly unverified release gates in plan.md. Generated artifacts are not approvals.
