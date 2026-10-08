# Feature Specification: Backup and Restore

**Feature Branch**: `004-backup-restore` (planning PR on `plan/milestone-14-backup-restore`)

**Created**: 2026-10-08

**Status**: Done (2026-10-08), implemented in PRs #132–#136; SC-006 passed, SC-005 skipped (tasks.md). *Originally:* Draft for owner review. Nothing here is approved for implementation until the owner merges this plan (Principle VII).

**Input**: GitHub issue #129, "Data export/backup and import/restore": *Users should be able to backup their sessions. Add options to backup and restore data. Backup (=export) should also allow user to choose export file format (for example user wants to move data to Excel). If possible/feasible show last backup date.*

**Builds on**: the notebook export of `specs/001-privacy-account-lifecycle` (user story 3, `POST /account/export`, format version 1), which today is mapped only behind `PRIVACY_LIFECYCLE_ENABLED` and so is not available in production. Spec 001 lists import as out of scope; this feature adds it.

## Clarifications

### Session 2026-10-08

- Q: What does restoring a backup do to the notebook that's already there? → A: **Adds the missing pages.** Pages from the file that aren't in the notebook are added; exercises are matched by name. Nothing already in the notebook is changed or removed, so restoring the same file twice adds nothing the second time.
- Q: Which formats can a copy be taken in? → A: **JSON and CSV.** JSON is the full backup and the only format that can be restored. CSV is one row per set, for Excel and other spreadsheets. No XLSX, which would need a new dependency.
- Q: Where does "last backup" come from? → A: **The server, on the account.** A completed JSON backup records its time on the account, so every device shows the same date. This amends spec 001's "No export record/history is persisted" (plan D6), and the time is removed with the account.
- Q: Where does it live, and does it wait for the privacy flag? → A: **Its own screen, off the cover, not behind the flag.** Privacy & account links to it instead of having its own export screen.
- Q: Ship the backup outside the privacy flag before spec 001 releases? → A: Yes, once the processing-decision amendment is signed. The export's deployed-transport gate has already passed.
- Q: Does restore need the current password? → A: No. It only adds pages, like logging them.
- Q: How large a backup can be restored? → A: 25 MB, configurable.
- Q: Which CSV columns? → A: As in the API contract: recorded values only, one row per set, no e1RM.
- Q: Show the last-backup date on the cover? → A: No, only on the Backup & restore screen.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Keep a backup of my notebook (Priority: P1)

As a lifter with a year of sessions in the notebook, I can take a complete copy of it as a file I keep myself, so a lost account, a mistaken deletion or the service going away doesn't take my log with it.

**Why this priority**: A paper notebook sits on the lifter's shelf. This one exists only on a server the lifter doesn't control, and today they can't take a copy at all, because the export is behind an unreleased flag.

**Independent Test**: From the cover, open Backup & restore, choose Full backup, enter the password and download. The file is the format-version-1 export from spec 001 and opens as valid JSON. The screen and the cover then show the new last-backup time, and so does a second browser.

**Acceptance Scenarios**:

1. **Given** a signed-in user on the cover, **When** they choose **Backup & restore**, **Then** they see when they last took a full backup ("Last backup 3 October 2026, 09.42", or "No backup yet") and the two ways to take a copy, with Full backup selected.
2. **Given** Full backup is selected, **When** they enter their current password and choose **Download backup**, **Then** the same checks, progress, cancel and failure states as spec 001's export apply, and a complete file is saved as `gym-notebook-YYYY-MM-DD.json` (the local date).
3. **Given** a full backup finished sending, **When** the screen shows completion, **Then** the last-backup line shows the new time, and the account records it.
4. **Given** a backup failed, was cancelled or was cut short, **When** the user looks at the last-backup line, **Then** it is unchanged.
5. **Given** the privacy flag is off, **When** a user takes a backup, **Then** it works the same. Its `privacyRecords` are null, as they are for any account without those records.

---

### User Story 2 - Open my log in a spreadsheet (Priority: P2)

As a lifter who wants to make my own charts or totals, I can download my sessions as a spreadsheet file that Excel opens straight into columns.

**Why this priority**: It's the issue's own example. The JSON file is complete but not something to open in Excel.

**Independent Test**: Choose Spreadsheet (CSV), download, and open the file in Excel with Finnish regional settings and with US settings. In both, each column lands in its own cell, numbers are numbers, and "Sääriprässi" reads correctly.

**Acceptance Scenarios**:

1. **Given** Spreadsheet (CSV) is selected, **When** the user downloads, **Then** they get `gym-notebook-YYYY-MM-DD.csv` with one row per set, oldest page first, in the columns of the API contract's CSV section.
2. **Given** a page with a block that has no sets, or a page with no blocks, **When** it is written to CSV, **Then** it still gets one row with the set columns empty, so no page or block vanishes.
3. **Given** the browser's language writes decimals with a comma (Finnish, for example), **When** the CSV is written, **Then** it uses `;` between fields and `,` in decimals; otherwise `,` and `.`. Dates are `YYYY-MM-DD` and times `HH:mm` in the browser's time zone, which spreadsheets recognize.
4. **Given** a CSV download completes, **Then** the last-backup line doesn't change, and the screen says a spreadsheet can't be restored.

---

### User Story 3 - Restore a backup (Priority: P1)

As a lifter who deleted a page by mistake, or who is moving to a new account, I can choose a backup file and get back the pages that aren't in my notebook, without risking what's already there.

**Why this priority**: A backup is only worth something if it can be brought back. Without restore, the file is an archive, not a backup.

**Independent Test**: Take a full backup, tear out two pages, restore the file. The two pages come back with all their blocks and sets, everything else is untouched, and the summary says "Restored 2 pages … 48 pages were already in your notebook." Restore the same file again: "Nothing to restore."

**Acceptance Scenarios**:

1. **Given** the Restore section, **When** the user chooses a file, **Then** the app reads it on the device and, before anything is sent, shows what it holds: when the backup was taken, how many pages (with their first and last dates), sets and exercises. It then offers **Restore missing pages** and **Choose another file**.
2. **Given** a valid backup, **When** the user confirms, **Then** every page in the file that the notebook doesn't already have is added with its blocks and sets in their order, in one step. A summary then says how many pages, sets and new exercises were added, and how many pages were already there.
3. **Given** a page in the file whose start time matches a page in the notebook, **Then** it counts as already there and is left alone, even if its sets differ. Restore never edits or deletes an existing page.
4. **Given** an exercise in the file with the same name as one in the notebook (by the same rule rename and merge already use), **Then** its sets are added under the existing exercise. A new name creates the exercise with the file's Bodyweight/Loaded classification. Where the two classifications differ, the notebook's is kept and the summary names the exercise.
5. **Given** the file isn't a Gym Notebook backup, is a CSV, is damaged or cut short, has broken references, or has a format version this app doesn't read, **Then** nothing is sent or nothing is written, and the screen says which case it is and that the notebook hasn't changed.
6. **Given** a failure during restore (server busy, connection lost, session ended), **Then** nothing from the file is in the notebook (all or nothing), and retrying is safe.
7. **Given** the privacy flag is on and the account hasn't allowed optional workout details, **When** the file has titles, gyms, notes or bodyweight, **Then** the pages are restored without them, and the summary says so and links to Optional workout details.

---

### User Story 4 - Know when I last backed up (Priority: P3)

As a lifter, I can see when I last took a full backup, so I know whether it's time for another.

**Acceptance Scenarios**:

1. **Given** a full backup taken on my phone, **When** I open Backup & restore on my laptop, **Then** it shows the same date and time.
2. **Given** no full backup was ever taken, **Then** the line reads "No backup yet." It never nags, counts days or turns red (Product principle 5).

### Edge Cases

- **A file from another account** restores like one's own: only the notebook content is used. The file's account details, ids and privacy records (notice acknowledgement, consent) are never applied; consent isn't transferable.
- **File ids are only references inside the file.** They are never used to look anything up in the database, so a hand-edited file naming another user's exercise or page id can't reach it.
- **Restoring the same file twice**, or two backups that overlap, adds only what's missing. Two pages inside one file with the same start time are both added (the original notebook had both).
- **An exercise renamed since the backup** comes back under its old name as a separate exercise; the existing rename and merge put it back together. The summary lists new exercises by name so this is visible.
- **An unfinished page in the file** (no end time) is restored unfinished and shows in Sessions as in progress, as it was.
- **An empty notebook in the file** (no pages) restores nothing and says so; unused exercises in the file aren't created.
- **A large file**: the reference notebook (1,000 pages × 10 blocks × 10 sets) is in range. A file over the size limit is refused before upload with the limit in the message.
- **Deletion or a password change during restore**: restore takes the account's shared lifecycle lock like any write, so deletion waits for it or the restore gets 401; nothing orphaned remains.
- **A backup taken while durable logging (spec 003) is saving a page elsewhere** reflects one consistent moment, as spec 001 already guarantees. Restored pages start at revision 1.
- **CSV text that looks like a formula** (an exercise or note starting with `=`, `+`, `-`, `@`, tab or carriage return) is written so a spreadsheet shows it as text and never runs it.
- **CSV values with the separator, quotes or line breaks** (notes) are quoted per RFC 4180.
- **A bodyweight exercise's weight** in CSV is the added load, blank when none, as in the app; a blank weight is never written as 0.

## Requirements *(mandatory)*

### Functional Requirements

**Screen and placement**

- **FR-001**: A **Backup & restore** screen at `/backup`, under the auth guard, linked from the cover. Like the export screen it replaces, it sits outside the notice gate, so a copy can be taken without first continuing past a notice. It shows the last-backup line, a Make a backup section and a Restore section.
- **FR-002**: Spec 001's Take a copy screen (`/account/export`) is replaced by this screen: the route redirects to `/backup`, and the export links in Privacy & account, the deletion review and the consent withdrawal review point here.

**Backup (export)**

- **FR-003**: The notebook export (`POST /account/export`) is mapped whether or not `PRIVACY_LIFECYCLE_ENABLED` is on. Its password check, snapshot, streaming, guards, rate limits and one-export-per-account rule are unchanged (spec 001 FR-008 to FR-013).
- **FR-004**: The user chooses the format before downloading: **Full backup (JSON)**, the default, or **Spreadsheet (CSV)**. Both need the current password. The request says which was chosen.
- **FR-005**: A JSON backup is the export file unchanged, format version 1, plus `account.lastBackupAt` (the previous backup's time, or null). Saved as `gym-notebook-YYYY-MM-DD.json`.
- **FR-006**: A CSV is built on the device from the same export snapshot. It has a UTF-8 byte order mark, CRLF line ends, one header row, then one row per set as specified in the API contract. Saved as `gym-notebook-YYYY-MM-DD.csv`.
- **FR-007**: CSV dialect follows the browser's number format: `;` and decimal comma where the locale's decimal separator is a comma, otherwise `,` and decimal point. Text cells starting with `=`, `+`, `-`, `@`, tab or carriage return are prefixed with `'`.
- **FR-008**: Computed values (e1RM, volume) are not in either file. The records they're computed from are (spec 001 FR-011; Product principle 2).

**Last backup**

- **FR-009**: When the server has sent the last byte of a JSON backup, it records that time on the account as `LastBackupAt`. Failed, cancelled or cut-short exports and CSV exports don't change it.
- **FR-010**: `GET /account/backup` returns `{ lastBackupAt }` for the signed-in account. The screen shows it in local time ("3 October 2026, 09.42") or "No backup yet.", and refreshes it after a backup completes.
- **FR-011**: `LastBackupAt` is part of the account: included in the export, removed by account deletion, and listed in the processing decision (plan D6).

**Restore (import)**

- **FR-012**: The user chooses a `.json` file. Before upload, the app checks on the device that it parses, is format version 1 and is within the size limit, and shows its summary (US3 scenario 1). Nothing is sent until the user confirms.
- **FR-013**: `POST /account/restore` takes the backup file as the request body. It validates the whole file on the server: format version, every reference resolving inside the file, and every value meeting the rules of the existing workout, block and set routes. An invalid file gets 400 `backup_invalid` with a reason code and no echo of file content, and nothing is written.
- **FR-014**: A page is already in the notebook when the account has a workout with the same `startedAt` instant. Such pages are skipped whole. Every other page is added with its date, times, heading fields, blocks in position order and sets in set-number order, with new ids.
- **FR-015**: Exercises are matched by normalized name within the account (`ExerciseNameNormalizer`). An unmatched exercise used by an added page is created with the file's name and classification. Exercises only used by skipped pages, or by no page, aren't created. A matched exercise keeps its own classification.
- **FR-016**: The whole restore is one transaction under the account's shared lifecycle lock. It either adds everything it reports or nothing. At most one restore runs per account (429 `restore_in_progress`).
- **FR-017**: With the privacy flag on and no optional-details consent, the four optional details are dropped from restored pages, and the response counts them. File privacy records are never applied (consent and acknowledgement are the account's own).
- **FR-018**: The response reports pages added, sets added, exercises created (names), pages already present, exercises whose classification differed (names) and optional details dropped. The screen turns it into a sentence summary with a link to Sessions.
- **FR-019**: Restore is rate limited per account and refuses bodies over the size limit (plan D9, proposed 25 MB) with 413 before reading them.
- **FR-020**: Restore needs a valid session, not the password: it only adds, like logging a page (plan D8; owner, 2026-10-08).

**States and copy**

- **FR-021**: Backup keeps spec 001's export states (checking password, preparing file with Cancel, complete, recoverable failure). Restore has: choosing, reading the file, summary, restoring (indeterminate progress, no cancel once sent), done, and failure. Every failure says the notebook hasn't changed. Status lines are polite live regions; errors are `role="alert"`.

### Key Entities

- **User** (changed): gains `LastBackupAt` (instant, nullable).
- **Backup file**: the spec 001 export, format version 1, with `account.lastBackupAt` added. The input to restore.
- **Restore result**: counts and names reported back; not stored.

## Success Criteria *(mandatory)*

- **SC-001**: Round trip: export the reference notebook (1,000 pages × 10 blocks × 10 sets with every field used), restore it into an empty account, and export again. Notebook content (pages, blocks, sets, exercises and every value) matches field by field apart from ids and creation times (automated test, real Postgres).
- **SC-002**: Restoring the same file a second time adds zero rows and changes zero rows (automated).
- **SC-003**: Restoring the reference notebook into an empty account completes within 60 seconds locally (fixture timing, like 001's export check).
- **SC-004**: No invalid, forged or foreign-id file writes anything or reveals another account's data, and a failure mid-restore leaves zero added rows (automated).
- **SC-005**: The CSV opens in Excel with Finnish and with US regional settings, and in Numbers or LibreOffice: columns split correctly, numbers are numeric, non-ASCII text is intact, and no cell is evaluated as a formula (owner check, recorded with versions).
- **SC-006**: Owner walkthrough on a phone and keyboard-only on desktop: take a full backup, a CSV, and restore two torn-out pages, each in under two minutes excluding download time.

## Out of scope

- Importing CSV or other apps' formats (Strong, Hevy). Restore reads Gym Notebook's own JSON only.
- Replacing the notebook with a backup, or choosing a restore mode. Restore only adds.
- Merging a backed-up page into an existing page with the same start time.
- Scheduled or automatic backups, backups stored by the service, email reminders.
- XLSX or other spreadsheet formats.
- Exporting computed figures (e1RM, volume).

## Assumptions and dependencies

- Spec 001's export code (`NotebookExport.cs`, `ExportData.tsx`, `api/download.ts`) is the base. Its deployed-transport gate has passed (T053 spike Part B and SC-003, 2026-09-29), so taking it out of the flag adds no untested path to production. 001's open gates (notice content, suppliers, retention evidence) don't depend on the export. The owner accepted shipping it unflagged (plan Q1).
- The privacy processing decision (P1/P2) needs an amendment for `LastBackupAt` and for restore's processing, signed by the owner before release (Principle V: email and new account records need their own reviewed change). Browser storage (P5) is unchanged: a chosen file is read in memory and never stored.
- Durable logging (spec 003) is independent. If it lands first, restored pages start at revision 1; if after, its migration defaults them to 1.
