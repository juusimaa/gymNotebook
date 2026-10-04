# Retention schedule

The operator's retention schedule for the privacy and account lifecycle feature (specs/001 FR-018, [contracts/operations.md → Retention schedule contract](../../specs/001-privacy-account-lifecycle/contracts/operations.md#retention-schedule-contract)).

Owner/reviewer: Jouni Uusimaa. Version: draft 2026-09-28. The product maxima below are requirements, **not verified provider guarantees**. The live-setting observations are dated 2026-09-28; the disposal and agreement checks in T076–T077 remain open. Keep this schedule aligned with [suppliers.md](suppliers.md), [processing-decision.md](processing-decision.md) and the final notice before enabling the feature.

## Rules and schedule (FR-018–FR-021)

Use the original UTC event for every deadline. A copy, restore, migration, delayed ingestion or retry does not restart its clock. For a deleted person's data, calculate the backup deadline from the **original deletion boundary**, not from the date a backup/branch was made. Schedule cleanup and proof before the maximum. Where a provider cannot meet a limit, stop that processing/storage choice or obtain a reviewed specification and notice amendment before release. The owner keeps dated evidence outside Git and records a reference in [release-checklist.md](release-checklist.md).

| Category and purpose | Start event and maximum | Disposal/control, owner and present evidence |
| --- | --- | --- |
| Active account, credentials, exercises, workouts, blocks, sets and latest notice acknowledgement; provide the notebook | Creation until the person removes the data or completed account deletion; no grace-period active archive | Operator. `AccountDeletion.cs` removes workouts, then exercises, then User in one transaction; block/set cascades follow. Real-Postgres tests cover active removal; isolated restore and deployed reference proof remain T074/T081. |
| Optional workout title, location, notes and bodyweight; user-chosen context that can reveal health information | Until account deletion, individual removal, consent withdrawal, or the existing-user transition deadline, whichever comes first. The transition deadline is 30 calendar days after flag-on (T084) for accounts without consent. | Operator. Withdrawal and the one-time SQL below clear all four fields. Keep consent version/time only while consent remains and the account exists; withdrawal clears the pair. T097 has a real-Postgres SQL test; dated production counts and restored-copy handling remain open. |
| Transient export readers/buffers; deliver the user's file | Request lifetime, including abort/invalidation; **no persistent server copy** in the selected design | Operator/API. Dispose on completion/abort; T053/T081 must test deployed delivery. If a future persistent spool is introduced, it needs its own design: at most 24 hours from creation and cleanup on deletion. A file already downloaded to the user's device is outside service control. |
| Browser JWT and app-owned draft state; session and editor continuity | JWT grants access for 30 minutes from issuance. Local browser storage remains until sign-out or observed invalidation/cleanup; no server-held JWT copy exists. The unsaved editor draft (`gymnotebook.draft.*` in `sessionStorage`, P5 re-signed 2026-10-04) lasts until it is saved or discarded, sign-out, account deletion, another tab signing out, a non-expiry 401, or the tab closing, whichever comes first. A plain token expiry holds it in that tab for the same user's next sign-in. | User's device plus frontend code. Sign-out/401 cleanup is tested, but expiry of a token does not prove its bytes have been erased from an offline browser. Do not present 30 minutes as a browser-storage disposal guarantee. The draft's clearing rules are unit-tested (`editorDraftStorage.test.ts`, `invalidation.test.ts`), and the browser deletes `sessionStorage` when the tab closes. A browser that restores closed tabs may restore the draft too. |
| In-memory authentication/password rate counters; limit abuse | Creation until configured replenishment/eviction or process termination | Operator/API. Not persisted. Verify the live limiter's client address and behavior in T075; no disk-retention claim follows from this row. |
| Identifying operational/security console logs; operate and secure the service | Original collection **+30 calendar days maximum**. After account deletion, only lines with a reviewed continued-retention justification may remain to that original deadline. | Operator/Azure. Restrict access; no notebook content or credentials. Erase/anonymize non-justified identifying lines on deletion or remove that collection before release. Live workspace and console/system tables read 30/30 with `immediatePurgeDataOn30Days: true`; content, ingestion delay, other sinks, per-table purge behavior and observed disposal are still T075–T077. |
| `deletion.intent`, `deletion.committed`, `deletion.rolled_back` lines (UUID, boundary, event); suppress resurrection during restore fallback | Original **log collection +30 calendar days maximum** as identifying logs. Also no later than the FR-020 ceiling of **deletion boundary +31 calendar days**; use the earlier deadline. | Operator/Azure. No username, notebook content or credentials in the intended line. Continued-retention basis in P4 of [processing-decision.md](processing-decision.md) still needs owner sign-off. T075 checks actual content; T074 verifies fallback; T077 observes expiry. A missing or gapped source keeps restore access closed. |
| Neon point-in-time history and any backup, snapshot, manual dump or recovery-only replica; recover from failure | Each deleted person's original deletion boundary **+30 calendar days maximum** across every usable source/copy | Operator/Neon. On 2026-09-28 project `dawn-pine-04463679` reported `history_retention_seconds: 21600` (**6 hours**), one branch and no snapshots. No repo backup script was found; provider internal durability copies, external manual dumps, actual history expiry and restore eligibility still need T076–T077 evidence. A six-hour setting does not itself prove every copy expires in six hours. On 2026-09-29 Neon refused a schema read 7 hours back and allowed one at 1 hour (research R7), which shows customer-side access stops at the window; internal durability copies remain the accepted residual unknown. |
| Live duplicate or service-controlled non-production account copy; testing/operations | Remove at active deletion or do not create it. A strictly recovery-only source follows the original backup deadline above. | Operator. Tests use synthetic data; inventory other copies before release. No usable development notebook may keep a deleted person's data. T076 verifies branch permissions and copy inventory. |
| Preserved pre-restore Neon branch; reconcile the restored state | Created only during a restore; delete after reconciliation is verified **before ingress reopens**. The original data deadlines still apply. | Operator. [restore.md](restore.md) requires branch deletion and evidence. It holds other people's live records and its own history; never keep it as a standing backup. T074 exercises this. |
| Rights-request correspondence and minimal register; answer and demonstrate handling | Receipt through **12 calendar months after closure** for routine cases, Q8 approved 2026-09-28 | Operator. Restricted Proton Mail folder for correspondence; FileVault-encrypted local register with cloud sync disabled. Delete the Proton emails, including Trash, by closure + 11 months so Proton's up-to-30-day backups expire before the limit. Erase the register by the 12-month absolute deadline. A longer hold needs a specific reviewed obligation/dispute and an end condition, recorded separately. Verify mailbox/provider and local backup disposal before first use; see [rights-requests.md](rights-requests.md) and T076. |

**Azure 90-day metadata tables:** the live table scan on 2026-09-28 found only `AzureActivity` and `Usage` at 90/90; Azure rejected 30-day settings for them in T069. `Usage` is workspace billing/volume metadata, and `AzureActivity` had no rows in the earlier R7 inspection. They are outside the 30-day **app-user** log limit only while they contain no app-user data. T076 must confirm current routing/content and record their provider role. A new diagnostic route or user-identifying content in either table reopens this decision; it is not an approved FR-021 exception for app-user information.

**2026-09-29 T076 follow-up:** `AzureActivity` still had zero rows, while all 246 `Usage` rows referred only to the two Container Apps log tables as volume `DataType`; no other workspace tables contained rows. This supports the current 90-day metadata classification, subject to routing drift and T077 disposal observation. [Proton's public Mail policy](https://proton.me/mail/privacy-policy) says offline backups may remain up to 30 days. Q8's 12-month **absolute** deadline includes those copies, so deleting live correspondence on the deadline itself is insufficient. The owner must adopt an earlier active-deletion date with verified trash/backup behavior, or approve a reviewed Q8 change before real cases use this mailbox; no longer-than-limit exception has been approved.

**Resolved 2026-09-29 (owner decision):** Proton deletion, including Trash, moves to **closure + 11 calendar months**, so the 30-day backup window ends within Q8's 12-month maximum. Q8 itself is unchanged. Access is web-only, so no local mailbox copies exist. The owner also confirmed that no Mac backup covers the register; its only copy is the local file, kept outside the GitHub repository. Proton's account-specific terms remain open under T076.

**FR-021 exception register:** no longer-than-limit app-user retention is approved. It is not yet possible to certify that no such retention exists: Azure's actual disposal still needs T077's observation. Proton mailbox deletion was resolved on 2026-09-29 (closure + 11 months). Provider-internal copy timing (Neon durability copies, Azure internals, Cloudflare DNS metadata) was accepted by the owner on 2026-09-29 as a residual unknown governed by each DPA's deletion commitment ([agreement review](suppliers.md#t076-agreement-review-2026-09-29)). It is not an approved longer-than-limit exception, and the notice must not promise a provider-side erasure date. If discovered, record the precise information, obligation/claim, owner, review date, access controls and absolute end condition; amend the specification and notice before rollout. Do not use an indefinite default.

## Verification and drift checks

The operator rechecks the live Azure workspace flag, every table's interactive and total retention, diagnostic sinks and retained row ages; Neon's plan, history setting, branch/snapshot inventory and internal-copy evidence; and the rights mailbox/register disposal. Keep the command/result date and reviewer in restricted evidence. [Azure's retention guidance](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-retention-configure) distinguishes configured retention from purge behavior. Neon's [restore guidance](https://neon.com/docs/postgres/backup-restore/branch-restore) limits restore to available history but does not document this project's internal copies. T077 must observe boundary disposal, including a source that is too old to restore. Any new Neon branch, snapshot, plan, history window or manual dump reopens the restore-evidence sizing in [research R6](../../specs/001-privacy-account-lifecycle/research.md#r6--independent-restore-evidence).

Example: a justified identifying line collected 20 days before account deletion has at most 10 days left. Its expiry is not moved to 30 days after the deletion. Do not claim a 30-day guarantee from the Azure workspace declaration alone: ingestion delay, table overrides, archive, other sinks and existing data can change the actual deadline.

## Optional-details transition clearing (FR-035)

**What:** clear the title, location, notes and bodyweight of every workout whose account holds no optional-details consent. These are details stored before the consent flow existed, from accounts that never answered the transition question.

**When:** once, on the transition deadline: 30 calendar days after `PRIVACY_LIFECYCLE_ENABLED` was switched on in production (tasks.md T084), counted as UTC dates. The dated step in [release-checklist.md](release-checklist.md) holds the exact date once T084 is done. Run it on that day, not later. An account's details must not outlive the deadline because the step was late.

**Who and where:** the operator, in the Neon SQL editor against the production branch. No API route or admin screen exists for this, by design (data-model.md → Transition clearing), like the suspension marker's manual SQL.

**Records:** only the two numbers the statement returns, plus the follow-up query's result, in release-checklist.md. No account ids, usernames or cleared values.

### 1. Clear

Run as one transaction:

<!-- transition-clearing-sql -->
```sql
BEGIN;

-- Give up after 5 s rather than queue behind a long-running request. A lock timeout or a
-- deadlock (for example with an account deletion in progress) aborts the whole
-- transaction, so nothing is cleared: run the block again.
SET LOCAL lock_timeout = '5s';

WITH unconsented AS (
    -- Lock every account without consent. A consent grant racing this step either
    -- commits first, and the recheck under READ COMMITTED skips that account, or waits
    -- until this transaction commits and then consents with the details already cleared.
    -- A workout save needs the same row (OptionalDetails.cs), so none can slip in between.
    SELECT id
    FROM users
    WHERE optional_details_consent_version IS NULL
    FOR UPDATE
),
cleared AS (
    UPDATE workouts AS w
    SET title = NULL, location = NULL, notes = NULL, bodyweight_kg = NULL
    FROM unconsented AS u
    WHERE w.user_id = u.id
      AND (w.title IS NOT NULL OR w.location IS NOT NULL OR w.notes IS NOT NULL OR w.bodyweight_kg IS NOT NULL)
    RETURNING w.user_id
)
SELECT count(DISTINCT user_id) AS accounts_cleared,
       count(*)                AS workouts_cleared
FROM cleared;

COMMIT;
```

Record `accounts_cleared` and `workouts_cleared`. Zero for both is a valid result: it means every account with details answered the question before the deadline.

### 2. Verify

<!-- transition-verify-sql -->
```sql
SELECT count(*) AS workouts_with_details_without_consent
FROM workouts AS w
JOIN users AS u ON u.id = w.user_id
WHERE u.optional_details_consent_version IS NULL
  AND (w.title IS NOT NULL OR w.location IS NOT NULL OR w.notes IS NOT NULL OR w.bodyweight_kg IS NOT NULL);
```

The result must be `0` (SC-008). Record it. If it isn't `0`, run step 1 again and investigate before recording the step as done: with the flag on, the API rejects every non-empty detail from an account without consent, so a non-zero count after step 1 means something wrote around the API.

### Copies outside the live database

The cleared values stay in Neon's point-in-time history until it ages out. They are never restored into a notebook: a restore follows the restore runbook (restore.md, tasks.md T072), and its reconciliation must re-run step 1 if the restore point is earlier than this clearing. The general backup limit and its evidence belong to the schedule above (T071).

The SQL blocks above are run against real PostgreSQL in CI by `TransitionClearingSqlTests`, which reads them from this file between the `transition-*-sql` markers. Edit them here, and the test runs the new text.
