# Account restore runbook

Owner: Gym Notebook operator. Version: draft 2026-09-28. Review this runbook after every restore exercise and every change to the database, deletion flow, logging, Neon history or deployment configuration. The [restore contract](../../specs/001-privacy-account-lifecycle/contracts/operations.md#restore-contract--fail-closed) defines the required outcome; this runbook is its operator procedure.

**Release status:** this procedure has not passed the isolated Neon exercise (T074). Do not treat this document as evidence that production restores are safe. Use only with a reviewed incident record and an authorized operator. Never rehearse with real users' data. Keep account UUIDs, hashes, connection details, logs and incident evidence in restricted storage, outside Git.

## Stop conditions and incident record

Keep API ingress disabled if any precondition, evidence source, comparison, SQL result, secret rotation or verification is uncertain. An empty query result does not prove that its source is complete. Do not choose a different restore point, discard the preserved branch or reopen to make an uncertain result disappear.

Start a restricted incident record with operator/reviewer, UTC start and target `T`, reason, Neon project and branch IDs, current revision/image, schema migration before and at `T`, history window, every restore/copy source, ingress state, evidence source and its covered interval, counts and verification results. Record the decision to reopen and the time of each action. Store only references and aggregate results in the repository's release checklist.

The restored target must contain the `privacy_account_id` migration (`20260925063835_AddPrivacyAccountIdentity`) and a compatible schema for the running API. Reject a pre-UUID target until a separately reviewed migration and reconciliation plan exists. The UUID, not the integer user ID or username, is the account identity for this procedure. Do not generate replacement UUIDs for old data.

## 1. Isolate and select the source

1. Stop deployments, scheduled jobs and all other database writers, including direct operator sessions. Disable **API** ingress before accessing restore controls. The production app and resource group are named in `.github/workflows/deploy.yml`; verify the live names and subscription first. For the current deployment the command is:

   ```sh
   az containerapp ingress disable --name ca-gymnote-prod-58dd-api --resource-group rg-gymnotebook-prod
   ```

2. Verify that both the public API URL and its Azure FQDN are unreachable. Inspect active revisions, other ingress paths and background jobs. Drain active requests and confirm there are no remaining writers before capturing the pre-restore state. Keep ingress closed throughout reconciliation, credential rotation and cleanup. A new deployment may re-enable ingress because `infra/modules/container-app-api.bicep` declares it; hold the deployment workflow until reopening.
3. Record the exact UTC `T`, current UTC time, the latest applied migration (`SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC;`), the Neon plan/history window and the list of branches, snapshots and other copies. Confirm `T` lies inside the **currently verified** history window and is after the UUID migration. The six-hour window in [research R7](../../specs/001-privacy-account-lifecycle/research.md#r7--retention-is-more-than-configuration-intent) was a 2026-09-24 observation, not a permanent guarantee. A changed plan, history setting, snapshot, branch or manual dump triggers a new R6 retention and evidence review before proceeding.
4. Use a direct Neon connection for operator SQL. Keep the preserved branch available for the entire comparison. Capture its branch ID immediately after the restore. Restrict branch creation/restore access to the operator; T076 verifies that access before release.

## 2. Restore and preserve the previous state

Use Neon's current self-restore syntax, with a unique incident-specific branch name:

```sh
neon branches restore main '^self@<RFC3339-UTC-T>' \
  --preserve-under-name <incident-preserved-branch> \
  --project-id <verified-project-id>
```

For example, replace `<RFC3339-UTC-T>` with an exact timestamp such as `2026-09-28T10:00:00Z`. Check `neon branches restore --help` against the installed CLI before execution; [Neon's root-branch guide](https://neon.com/docs/postgres/backup-restore/branch-restore) uses `^self@...`. Do not retry after a timeout until the branch list and operation state show whether the first restore succeeded; a second restore can preserve the wrong state. If preservation failed or the branch cannot be read fully, use the fallback in step 4 only after proving its independent log coverage.

## 3. Primary reconciliation: preserved-branch diff

With ingress still closed, read the preserved branch's **complete** `users` table and stage only `privacy_account_id`, `password_hash` and `token_version` for comparison in the restored database. Use an approved restricted transfer channel; if using `psql` client-side `\copy`, write to an operator-only temporary location with restrictive permissions, never to the repository or a shared terminal transcript. In the preserved-branch session, run `SELECT count(*) FROM users;` and the following export, with a private file path:

```sql
\copy (SELECT privacy_account_id, password_hash, token_version FROM users ORDER BY privacy_account_id) TO '<private-file>' WITH (FORMAT csv)
```

Confirm the source branch ID, successful export/import, row count, uniqueness and transfer integrity before treating a zero-row diff as real evidence. Remove the temporary transfer copy once no longer needed; do not put hashes or UUID lists into the incident summary.

In a fresh operator session against the restored branch, create a staging table and import the three columns in that order. The `\copy` line is a `psql` command, not SQL; substitute the approved private file path. Keep the session open through verification.

```sql
CREATE TEMP TABLE preserved_users (
    privacy_account_id uuid PRIMARY KEY,
    password_hash text NOT NULL,
    token_version integer NOT NULL
) ON COMMIT PRESERVE ROWS;

-- psql client command after a complete export from the preserved branch:
\copy preserved_users (privacy_account_id, password_hash, token_version) FROM '<private-file>' WITH (FORMAT csv)

SELECT count(*) AS staged_accounts FROM preserved_users;
```

Compare `staged_accounts` with the count from the preserved source. The source query must select all rows, with no pagination or filter. A mismatch or import error closes this path. A restored UUID absent from `preserved_users` represents an account deleted after `T` under the [User-removal invariant](../../specs/001-privacy-account-lifecycle/data-model.md#restore-evidence-and-invariant). A UUID present only in the preserved branch belongs to an account created after `T`; do not manufacture it in the restored database. In particular, a reused username never matches by name.

Apply the re-deletion and credential copy as one transaction. Inspect the candidate count against the incident timeline before committing. `workouts` cascade to workout exercises and sets; exercises must go next because a block can reference them with `RESTRICT`; `users` go last, as in `AccountDeletion.cs`. If the candidate set or source is suspicious, `ROLLBACK` and stay closed.

```sql
BEGIN;
CREATE TEMP TABLE restore_delete_ids ON COMMIT PRESERVE ROWS AS
SELECT u.id, u.privacy_account_id
FROM users AS u
LEFT JOIN preserved_users AS p USING (privacy_account_id)
WHERE p.privacy_account_id IS NULL;

SELECT count(*) AS accounts_to_redelete FROM restore_delete_ids;
-- Pause here to review the count. Issue ROLLBACK instead of the following statements if uncertain.
DELETE FROM workouts WHERE user_id IN (SELECT id FROM restore_delete_ids);
DELETE FROM exercises WHERE user_id IN (SELECT id FROM restore_delete_ids);
DELETE FROM users WHERE id IN (SELECT id FROM restore_delete_ids);

-- A password change after T must not be undone for an account that still exists.
UPDATE users AS u
SET password_hash = p.password_hash,
    token_version = p.token_version
FROM preserved_users AS p
WHERE u.privacy_account_id = p.privacy_account_id
  AND (u.password_hash, u.token_version)
      IS DISTINCT FROM (p.password_hash, p.token_version);
COMMIT;
```

Run the transaction again if it was interrupted before commit; it is safe to rebuild `restore_delete_ids` in a fresh session. A successful commit needs the checks in step 5. Keep the preserved branch until all checks pass. Do not let a SQL editor automatically commit the block one statement at a time.

## 4. Fallback when the preserved branch is unusable

Use this path only when the preserved state cannot be compared. Obtain the `ContainerAppConsoleLogs_CL` lines for the **actual API app** from `T` through verified isolation, including every revision that ran in that interval. The application writes `deletion.intent`, `deletion.committed` and `deletion.rolled_back` with `PrivacyAccountId=<uuid>` and `DeletionBoundaryAt=<UTC instant>`; see `AccountDeletion.cs`. Use the event and boundary in the line, not ingestion time alone, to classify a deletion. Check query time range, workspace, app filter, result limits, exports, duplicate/replayed lines and UTC parsing. Keep raw evidence restricted.

Before relying on any line set, prove ingestion coverage from `T` through isolation using independent collection/health evidence for the actual emitting revisions and workspace, including delayed ingestion and outages. Compare source and received/ingested times, workspace routing and retention. [Azure's ingestion guidance](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-ingestion-time) explains `TimeGenerated`, `_TimeReceived` and `ingestion_time()`. Deletion lines alone are emitted only on deletion, so an empty interval or continuous-looking result **cannot** establish gap-free delivery. If independent completeness evidence is unavailable, or if an interval may be missing, keep ingress closed. T074 must demonstrate this fallback and the gap case before release.

Reconcile by UUID **and deletion boundary** so a later retry cannot erase the meaning of an earlier intent. For each verified `deletion.committed` after `T`, re-delete its UUID if present. A committed line wins even if its intent line is missing. For an intent with a matching `deletion.rolled_back`, leave the account untouched for that attempt. For an intent with neither terminal line, do not guess: if its UUID exists in the restored database, suspend sign-in; if it is absent, record the missing row and keep access closed until the outcome is resolved. Conflicting lines or malformed identities also keep access closed. Never treat a missing committed line as proof of rollback.

Create `CREATE TEMP TABLE committed_delete_uuids (privacy_account_id uuid PRIMARY KEY) ON COMMIT PRESERVE ROWS;` in the restored-branch session and load **only** the distinct UUIDs with verified committed lines through the restricted operator session. Review the staged count and then run this transaction. A zero matched count must have an explained source. Do not stage intents. Repeating the transaction leaves already removed accounts absent.

```sql
BEGIN;
CREATE TEMP TABLE committed_delete_ids ON COMMIT PRESERVE ROWS AS
SELECT u.id, u.privacy_account_id
FROM users AS u
JOIN committed_delete_uuids AS c USING (privacy_account_id);

SELECT count(*) AS matched_accounts FROM committed_delete_ids;
-- Review the count before continuing; ROLLBACK if it cannot be explained.
DELETE FROM workouts WHERE user_id IN (SELECT id FROM committed_delete_ids);
DELETE FROM exercises WHERE user_id IN (SELECT id FROM committed_delete_ids);
DELETE FROM users WHERE id IN (SELECT id FROM committed_delete_ids);
COMMIT;
```

For an unresolved intent whose UUID **exists**, run this manual SQL against the restored branch while isolated. Replace the example UUID with the verified value in the restricted operator session. Record only the UUID reference and suspension time in the restricted incident record. Repeating it keeps the original suspension timestamp.

```sql
BEGIN;
UPDATE users
SET sign_in_suspended_at = COALESCE(sign_in_suspended_at, now())
WHERE privacy_account_id = '00000000-0000-0000-0000-000000000000'::uuid
RETURNING privacy_account_id, sign_in_suspended_at;
COMMIT;
```

Require exactly one returned row for an existing UUID. A correct password then gets `403 account_suspended`, and token validation also rejects the account. Resolve through the monitored privacy contact: verify the person's identity and the intent's outcome. If deletion was intended, remove its workouts, exercises and user in a transaction using the same order as step 3, then verify zero rows. If deletion was not intended, clear only that UUID's marker after the resolution is recorded:

```sql
BEGIN;
UPDATE users
SET sign_in_suspended_at = NULL
WHERE privacy_account_id = '00000000-0000-0000-0000-000000000000'::uuid
  AND sign_in_suspended_at IS NOT NULL
RETURNING privacy_account_id;
COMMIT;
```

The fallback cannot recover password hashes or token versions changed after `T`. Record affected/unknown scope and the security response in the restricted incident record and notice review; do not claim those password changes survived. JWT rotation in step 5 still revokes old tokens. A genuinely unresolved intent may remain suspended while other accounts reopen only if the log interval is proved complete and all other checks pass.

## 5. Verify, rotate credentials and reopen

1. With ingress closed, verify zero `users`, `workouts` and `exercises` rows for every re-deleted UUID/user ID using the session's `restore_delete_ids` or `committed_delete_ids` table. For example, `SELECT count(*) FROM users WHERE id IN (SELECT id FROM restore_delete_ids);` must return zero, and the corresponding queries for `workouts.user_id` and `exercises.user_id` must also return zero. In the primary path compare surviving users' `password_hash` and `token_version` to the preserved source without displaying values. Verify representative control accounts and their notebook records remain intact; in the exercise prove B's newer password works and the old one fails through a private test path. Check that every unresolved UUID has `sign_in_suspended_at` set; test sign-in through that same private path if available, otherwise check immediately after controlled reopening and close ingress again on failure. Check FK integrity, the schema/migrations expected by the deployed API, current privacy notice and consent configuration, and original retention deadlines. If `T` predates optional-details transition clearing, rerun [its clearing and zero-count check](retention.md#optional-details-transition-clearing-fr-035) before reopening.
2. Rotate `Jwt__Secret` on **every** restore, including a fallback. Generate a fresh high-entropy secret inside the operator's approved secret tool and enter it directly as the API Container App's existing `jwt-secret` in Azure; never print, log, commit, paste into a ticket, or put it in a shell command line. Restart every active API revision while ingress is disabled. [Container Apps requires a restart or new revision for an updated secret](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets). Verify the restarted revision is healthy while isolated; check that old tokens fail through a private test path or immediately after controlled reopening. The isolated exercise must include a token from an account registered after `T`. This rotation prevents a rewound integer ID/token version from accepting a prior token.
3. Before any later deployment, update the deployment's protected GitHub `JWT_SECRET` to the **same new value** through its secret UI, or change the deployment to reference the rotated secret without overwriting it. The current [deploy workflow](../../.github/workflows/deploy.yml) passes that secret to Bicep on each run. An old workflow secret can undo the emergency rotation. Verify the deployment path without exposing either value.
4. Record the evidence source, coverage, candidate/deleted/suspended counts, credential-copy result, rotation/revision result, retention checks, reviewer and explicit authorization to reopen. Delete the incident-preserved Neon branch **after** verification and **before** ingress returns. Verify its deletion and check for additional recovery copies. If deletion fails, keep ingress closed and resolve the branch and retention issue. Do not create a standing copy from it.
5. Re-enable API ingress using the captured live ingress configuration, then verify public and Azure routes, expected security settings, custom domains where applicable, `/health`, sign-in with a control account and rejection of an old token. Resume writers/deployments only after the credential source in step 3 is safe. If any check fails, disable ingress again and resume the runbook from the last verified step.

An interruption at any point leaves ingress disabled. On resumption, inspect the actual Neon branch and transaction states before replaying: never blindly rerun the restore command. SQL re-deletion, credential copying and suspension marking can be repeated after their source is reverified. If neither branch diff nor gap-free logs can be established, the affected restore remains unavailable; escalate the incident without reopening.
