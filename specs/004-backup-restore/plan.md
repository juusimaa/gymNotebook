# Implementation Plan: Backup and Restore

**Branch**: `004-backup-restore` | **Date**: 2026-10-08 | **Spec**: [spec.md](spec.md)

**Status**: Draft for owner review (constitution Principle VII). Decisions D1–D10 below are proposals until this plan is merged; the nine in the spec's Clarifications are the owner's (2026-10-08), including the answers to Q1–Q5 below.

## Summary

Give the notebook a backup the lifter keeps, and a way back. The spec 001 export becomes a **Full backup**, now outside the privacy flag, and its time is recorded on the account as the **last backup**. A **Spreadsheet (CSV)** is written in the browser from the same snapshot. **Restore** uploads a backup file and adds, in one transaction, the pages the notebook doesn't have. It never changes or removes what's there. One new screen, `/backup`, replaces `/account/export`. No new dependency.

See [data-model.md](data-model.md), [API contract](contracts/api.md), [UI contract](contracts/ui.md), the clickable [UI draft](ui-draft.html) and [tasks.md](tasks.md).

## Technical Context

**Language/Version**: unchanged: C# / .NET 10, TypeScript, React 19, Vite.

**Primary Dependencies**: none new. CSV is a few dozen lines of TypeScript; restore uses `System.Text.Json` and EF Core.

**Storage**: PostgreSQL. `users` gains `last_backup_at timestamptz null`. One migration. Restore inserts into the existing `exercises`, `workouts`, `workout_exercises` and `sets`.

**Testing**: xUnit + Testcontainers for the stamp, the restore rules (matching, skipping, all-or-nothing, foreign ids, consent dropping, the lifecycle lock) and the reference-notebook round trip, reusing 001's 100,000-set fixture. Vitest for the CSV writer (dialect, quoting, formula guard, empty pages, time zone) and the file pre-check/summary, both pure helpers. Owner checks for Excel (SC-005) and the walkthrough (SC-006).

**Constraints**: ownership (file ids never touch the database), the lifecycle lock, `Cache-Control: no-store`, no file content in logs or error bodies, no exception details.

## Constitution Check

| Principle | Status |
| --- | --- |
| I. Architecture | Pass. Minimal API routes in a `NotebookRestore` class beside `NotebookExport`, one column, no new layers, interfaces or packages. The CSV writer is a plain module in `screens/`, the API calls go in `api/backup.ts`. |
| II. Focused changes | Pass. Six PRs, listed in [tasks.md](tasks.md). |
| III. Learning | Pass. The restore is a readable sequence (validate → match → insert); the CSV writer is a pure function with its rules in comments. |
| IV. Verification | Pass. Restore is a persistence change, tested against real Postgres, including the round trip at reference size. |
| V. Security & privacy | Pass, with decisions to review. The export's password, guards and limits are kept. Restore adds a large-body write route: a size cap, a per-account limit and one-at-a-time (D9), and it never trusts file ids (D7). `LastBackupAt` is a new personal record: export, deletion and the processing decision cover it (D6). Leaving the flag puts the export in production while 001's other release gates (notice content, suppliers, retention evidence) are still open; accepted by the owner (Q1). |
| VI. Docs aligned | Pass. PLAN.md (API, Milestones), `docs/ui/README.md` and `prototype.html` (the new screen, the replaced export screen, the cover), README.md (the restore size setting), 001's export contract (the added field) and `docs/privacy/processing-decision.md` change with the behaviour. |
| VII. Reviewed plans | This document is the draft under review. |

## Design decisions

**D1 — One screen replaces Take a copy.** Two screens that both export the notebook would drift. `/backup` takes over the export UI (`ExportData.tsx` becomes `Backup.tsx`, keeping its state machine). `/account/export` redirects there, so 001's links and its docs keep working. Privacy & account keeps its "Export your notebook" block, linking here.

**D2 — The export leaves the flag; nothing else in 001 does.** `POST /account/export` is mapped always. The notice, consent, deletion routes and the notice gate stay behind `PRIVACY_LIFECYCLE_ENABLED`. With the flag off, `privacyRecords` are null as for any account without them. This is why `/backup` sits outside the notice gate (FR-001), as the export screen did.

**D3 — The format is one request field, `format: "json" | "csv"`.** The server streams the same JSON snapshot either way. The field only decides whether the completed stream stamps `LastBackupAt`. An absent field means `json`, so 001's client and tests are unchanged.

**D4 — CSV is written in the browser, from the JSON.** The server has instants in UTC. A spreadsheet user wants the times they saw, in their time zone, with their decimal separator, and only the browser knows those. The screen already reads and parses the whole file before saving it (001's complete-file check). `toCsv(exportFile, { timeZone, decimalComma })` is a pure function, so Vitest covers it without a browser. One server format, one field guide, no second streaming path.

**D5 — CSV dialect from the locale, not a setting.** `Intl.NumberFormat(locale).formatToParts(1.5)` tells whether decimals use a comma. Comma locales (fi, sv, de…) get `;` fields and `,` decimals, because that is what Excel expects there; others get `,` and `.`. Plus a UTF-8 BOM so Excel reads ä and ö, CRLF, RFC 4180 quoting, and a `'` prefix on text cells that start like a formula (OWASP's CSV-injection rule). No `sep=` line: Numbers and Google Sheets show it as data.

**D6 — `LastBackupAt` is stamped by the server after the last byte.** The stream already knows when the closing brace has gone out. After that, a short write on its own connection under a delivery guard sets `last_backup_at = now()` when `format` is `json`. It's after the file, so a cut stream never stamps. It's best-effort: if the write fails, the file is still good, the failure is logged without personal data, and the line shows the older time, which errs safe. The server can't know the browser saved the file, so the screen's copy says "finished", not "saved to your device". It amends 001's "No export record/history is persisted". This is a single timestamp on the account, not a history, exported and deleted with it.

**D7 — Restore treats the file as untrusted data, and ids as labels.** The body is parsed into DTOs with `System.Text.Json` (no polymorphism, max depth 8). File ids are used only to join the file's own arrays; every row is inserted with a new id and the caller's `UserId`. Values are checked with the same rules as `POST /workouts` and `PUT /workouts/{id}/exercises` (shared validation helpers, not copies). `account`, `privacyRecords`, `fieldGuide`, `createdAt` fields and `userId` fields are ignored. `CreatedAt` is the restore time.

**D8 — No password for restore.** Restore only adds pages, the same as logging them by hand, which needs only a session. A password step would also bring the per-account BCrypt throttle into a flow that doesn't need it. The trade-off: a stolen session could fill a notebook with junk pages, which it already can, one page at a time. *Owner, 2026-10-08: no password (Q2).*

**D9 — Limits.** Request body ≤ `Restore:MaxBytes` (default 25 MB; the reference notebook's export measured 10.4 MB, `docs/privacy/release-checklist.md`), enforced by Kestrel's per-endpoint limit before the body is read (413). One restore per account at a time via `pg_try_advisory_xact_lock` in a new "restore" namespace (429 `restore_in_progress`), mirroring the export. A per-account rate limit of 5 restores per 10 minutes. A 120 s statement budget, like export. *Owner, 2026-10-08: 25 MB (Q3).*

**D10 — Matching rules.** A page is "already there" when `startedAt` equals an existing workout's `startedAt` for the account. That is unique in practice, survives renames, and makes restoring the same file idempotent, because restored pages keep the file's `startedAt`. The lookup is one query over the file's distinct instants (`= ANY(@instants)`), not one per page. Exercises match by `NormalizedName` with the existing normalizer. Inserts use `AddRange` per table inside the transaction; the 100k-set case is measured in the fixture (SC-003) before reaching for `COPY`.

## Configuration

| Setting | Local (`.env`) | Azure | Notes |
| --- | --- | --- | --- |
| `Restore__MaxBytes` | optional | optional | new, plain env; default 26214400 (25 MB) |

## Resolved questions (owner, 2026-10-08)

- **Q1 — The export outside the flag.** The export's own technical gates have passed on the deployed stack: spike Part B (T053, 2026-09-29) and SC-003 (11.5 s for the 100,000-set notebook, `docs/privacy/release-checklist.md`). 001's still-open gates concern the notice content, suppliers, retention and the owner walkthrough, not the export. **Answer: ship unflagged**, once the processing-decision amendment (D6, T018) is signed.
- **Q2 — Password for restore.** **Answer: none** (D8).
- **Q3 — Size cap.** **Answer: 25 MB** (D9), about 2.5 times the reference notebook, configurable.
- **Q4 — CSV columns.** As in the [API contract](contracts/api.md#csv-columns): one row per set, page details repeated on each row, no e1RM. **Answer: as proposed.**
- **Q5 — Last backup on the cover.** **Answer: only on the Backup & restore screen**, to keep the cover calm.
