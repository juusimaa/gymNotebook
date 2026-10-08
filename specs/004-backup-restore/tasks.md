# Tasks: Backup and Restore

**Status**: PR 1 merged (#131, 2026-10-08). PR 2 backend implementation verified locally; awaiting owner review. AI implements by default; the owner names any task they will write themselves.

## PR 1 — Plan (this PR)

- [x] T001 `specs/004-backup-restore/` spec, plan, data model, contracts, UI draft and tasks.
- [x] T002 (owner) Answer plan Q1–Q5; fold the answers into the spec's Clarifications and the plan's decisions.

## PR 2 — The export leaves the flag, and records the last backup (backend)

- [x] T010 Map `POST /account/export` regardless of `PRIVACY_LIFECYCLE_ENABLED` (D2). Keep the notice, consent and deletion routes flagged.
- [x] T011 `format` on `ExportRequest` (`json` default, `csv`; anything else 400 `invalid_request`).
- [x] T012 Migration `AddUserLastBackupAt`; `LastBackupAt` on `User`. Review the generated migration.
- [x] T013 In `NotebookExport`, after the closing brace for `format: json`, stamp `last_backup_at` on its own connection under a delivery guard; log a failure without personal data (D6).
- [x] T014 `account.lastBackupAt` in the export and its explanation in `ExportFieldGuide.cs`.
- [x] T015 `GET /account/backup` → `{ lastBackupAt }`, `Cache-Control: no-store`.
- [x] T016 Tests: `Export_FlagOff_Returns200`, `Export_JsonCompletes_StampsLastBackupAt`, `Export_Csv_DoesNotStamp`, `Export_CutShort_DoesNotStamp`, `Export_WrongPassword_DoesNotStamp`, `Export_IncludesPreviousLastBackupAt`, `GetBackup_NoBackup_ReturnsNull`, `DeleteAccount_RemovesLastBackupAt`, `Export_UnknownFormat_Returns400`.
- [x] T017 Docs: 001's `contracts/api.md` (the added field, the flag, "No export record" amended), PLAN.md → API.
- [x] T018 (owner) Amend `docs/privacy/processing-decision.md` P1 for `LastBackupAt` and P2 for restore, and sign it **before PR 2 merges/deploys the unflagged export** (approved Q1; see clarification below).

**PR 2 verification (2026-10-08):** all 426 backend tests pass against real PostgreSQL, including flag-off export, omitted/invalid formats, JSON/CSV stamping rules, the previous timestamp, deletion, genuine client disconnect, time-limit truncation, post-flush invalidation and best-effort stamp-write failure. `dotnet format --verify-no-changes` and `git diff --check` pass. Independent review found no code blockers. The generated migration was reviewed: only nullable `users.last_backup_at`, no default or unrelated changes.

**T018 release gate clarification:** approved Q1 requires the signed processing-decision amendment **before the unflagged export ships**. Because `main` deploys automatically, this is a prerequisite to merging/deploying PR 2, superseding T018's original “before PR 6”. The owner approved both amendments on 2026-10-08; the signed decisions are recorded in `docs/privacy/processing-decision.md`. PR 3 must still verify that restore matches the approved description before it ships.

## PR 3 — Restore (backend)

- [ ] T020 Restore DTOs and `NotebookRestore.Validate`: shape, version, references, values through the same validation helpers as the workout routes (extract them where they're inline today, without changing behaviour).
- [ ] T021 `NotebookRestore.RestoreAsync`: shared lifecycle lock, restore advisory lock (new namespace), one transaction, the startedAt lookup in one query, exercise matching by `NormalizedName`, inserts per table, consent dropping (D7, D10).
- [ ] T022 `POST /account/restore`: per-endpoint `MaxRequestBodySize` from `Restore:MaxBytes`, the per-account rate limit, the responses in contracts/api.md.
- [ ] T023 Tests: `Restore_IntoEmptyAccount_AddsEverything`, `Restore_SameFileTwice_AddsNothing`, `Restore_ExistingStartedAt_SkipsPageUntouched`, `Restore_MatchingName_UsesExistingExercise`, `Restore_ClassificationDiffers_KeepsExisting`, `Restore_UnusedExercise_NotCreated`, `Restore_ForeignIds_NeverReachOtherUsers`, `Restore_BrokenReference_Returns400AndWritesNothing`, `Restore_InvalidValue_Returns400AndWritesNothing`, `Restore_UnsupportedVersion_Returns400`, `Restore_TooLarge_Returns413`, `Restore_NoConsentFlagOn_DropsOptionalDetails`, `Restore_FileConsent_NeverApplied`, `Restore_Concurrent_Returns429`, `Restore_DuringDeletion_LeavesNothing`.
- [ ] T024 Reference round trip and timing on the 100,000-set fixture (SC-001, SC-003).
- [ ] T025 Docs: PLAN.md (API, Auth → lifecycle coordination covers restore), README.md (`Restore__MaxBytes`).

## PR 4 — CSV writer and file pre-check (frontend helpers)

- [ ] T030 `screens/backupCsv.ts`: `toCsv(exportFile, { timeZone, decimalComma })` and `prefersDecimalComma(locale)` (D4, D5).
- [ ] T031 `screens/backupFile.ts`: parse and pre-check a chosen file, and summarise it (FR-012), with typed results for each failure case.
- [ ] T032 Vitest: dialects, quoting, formula guard, empty pages and blocks, null weight, bodyweight added load, time-zone boundaries around midnight, non-ASCII, the BOM; and every pre-check outcome.

## PR 5 — Backup & restore screen

- [ ] T040 `api/backup.ts`: `getLastBackup`, `exportNotebook(password, format)` (moved from `api/privacy.ts`), `restoreBackup(file)`.
- [ ] T041 `screens/Backup.tsx` and `Backup.css` from `ExportData.tsx`, per contracts/ui.md and the UI draft; dated file names.
- [ ] T042 Routes: `/backup` under the auth guard, outside the notice gate; `/account/export` redirects; cover link; Privacy & account, the deletion review and the withdrawal review link to `/backup`.
- [ ] T043 Docs: `docs/ui/README.md` (new screen, replaced export screen, cover), `docs/ui/prototype.html` (the screen and its states, from the draft).

## PR 6 — Release

- [ ] T050 (owner) SC-005: open the CSV in Excel with Finnish and US settings and in Numbers or LibreOffice; record versions and results.
- [ ] T051 (owner) SC-006: phone and keyboard-only walkthrough; record it.
- [ ] T052 PLAN.md → Milestones: milestone 14 entry with what it turned out to involve.
