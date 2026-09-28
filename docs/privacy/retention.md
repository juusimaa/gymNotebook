# Retention schedule

The operator's retention schedule for the privacy and account lifecycle feature (specs/001 FR-018, [contracts/operations.md → Retention schedule contract](../../specs/001-privacy-account-lifecycle/contracts/operations.md#retention-schedule-contract)).

> **Not yet complete.** The schedule itself (categories, start events, maximums, disposal evidence, the deletion log lines, the preserved pre-restore branch, Neon's 6-hour history window and the 90-day `AzureActivity`/`Usage` exception) is tasks.md T071. This file currently holds only the optional-details transition clearing step (T097), because user story 6 needed it first.

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
