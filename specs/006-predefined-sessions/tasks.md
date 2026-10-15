# Tasks: Predefined Sessions

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/api.md](contracts/api.md), [contracts/ui.md](contracts/ui.md), [quickstart.md](quickstart.md)

**Status**: Draft. Plan questions Q1–Q5 were resolved by the owner on 2026-10-15. AI implements by default; the owner names any task they will write themselves. Frontend tasks (T060 onward in each story) wait for the approved UI mock (T003).

**Tests**: Principle IV requires them. Backend tests use xUnit + Testcontainers (real Postgres 18), Arrange/Act/Assert, and are named `MethodName_Scenario_ExpectedResult`. Frontend helpers use Vitest. The existing suites must pass **unchanged** (SC-006); never edit an existing assertion to make it pass.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: US1 Prepare, US2 Tick off, US3 Adjust, US4 Templates (spec.md)
- **PR n**: the delivery PR from [plan.md → Delivery](plan.md#delivery). Each PR also updates the `PLAN.md` sections it changes.

Paths: backend `backend/GymNotebook.Api/`, backend tests `backend/GymNotebook.Tests/`, frontend `frontend/src/`.

---

## Phase 1: Setup

- [x] T001 Spec, plan, research, data model, contracts, quickstart and tasks in `specs/006-predefined-sessions/` (planning PR).
- [x] T002 Open the planning PR for `specs/006-predefined-sessions/` after owner approval of the plan (Principle VII); reference issue #165.
- [x] T003 **PR 1, UI mock**: with `/impeccable`, update `docs/ui/README.md` (Sessions, Session page, New page, a new Templates section) and `docs/ui/prototype.html` to cover everything in `specs/006-predefined-sessions/contracts/ui.md`:
  - prepared group and Start
  - prepare mode
  - done toggle and next set
  - finish summary
  - skipped sets
  - template list and editor
  - the SC-007 path

  Keep every "Rules the UI must not break" item. Light and dark. **Owner approval of this PR gates all frontend tasks.**

---

## Phase 2: Foundational: set status (PR 2, backend)

**Purpose**: The planned/done/skipped status and the "only done counts" rule. US1–US3 all depend on it. The frontend keeps working unchanged, because an omitted status means done.

- [x] T010 [P] Create `backend/GymNotebook.Api/SetStatus.cs`: enum `SetStatus { Planned, Done, Skipped }`.
- [x] T011 [P] Create `backend/GymNotebook.Api/WorkoutState.cs`: enum `WorkoutState { Prepared, InProgress, Finished }` and `static WorkoutState Of(DateTimeOffset? startedAt, DateTimeOffset? endedAt)`: prepared when `startedAt` is null, in progress when `endedAt` is null, otherwise finished. Comment why state is derived rather than stored (research R1).
- [x] T012 Create `backend/GymNotebook.Api/SetStatusRules.cs`, the one table from research R3:
  - `IsAllowed(WorkoutState, SetStatus)`:
    - prepared → planned
    - in progress → planned, done
    - finished → done, skipped
  - `DefaultFor(WorkoutState)`: planned when prepared, otherwise done.
  - Comment the table.
- [x] T013 Add `public SetStatus Status { get; set; } = SetStatus.Done;` to `backend/GymNotebook.Api/SetEntry.cs`.
- [x] T014 Configure `Status` in `backend/GymNotebook.Api/Data/AppDbContext.cs`:
  - a value converter to the lowercase strings `planned`/`done`/`skipped`
  - column `text not null default 'done'`
  - check constraint `ck_set_entries_status`: `status IN ('planned', 'done', 'skipped')`
- [x] T015 Generate migration `AddSetStatus` in `backend/GymNotebook.Api/Migrations/` (`dotnet ef migrations add AddSetStatus`). Review that it only adds the column with its default and the check constraint, then update `AppDbContextModelSnapshot.cs`.
- [x] T016 Add `string Status` to `backend/GymNotebook.Api/SetEntryResponse.cs` and `SetWriteResponse`, and map it in `GetWorkoutExercisesAsync` and the set routes in `backend/GymNotebook.Api/Program.cs`.
- [x] T017 Add optional `string? Status` to `PutSetInput.cs`, `CreateSetRequest.cs` and `UpdateSetRequest`. In `Program.cs`, parse it, apply `SetStatusRules.DefaultFor` when omitted (a PATCH without it keeps the current status), and return 400 `ErrorResponse("invalid_set_status")` when it is unknown or not allowed for the page's `WorkoutState.Of(...)`. Apply this in:
  - `PUT /workouts/{id}/exercises`
  - `POST /workouts/{id}/sets`
  - `PATCH /workouts/{id}/sets/{setId}`
- [x] T018 In `PATCH /workouts/{id}` (`Program.cs`), inside the same transaction:
  - **finish** (`endedAt` set on an in-progress page): `ExecuteUpdateAsync` every planned set of the page to skipped (FR-010).
  - **reopen** (`endedAt: null` on a finished page): every skipped set back to planned (Q1).
  - Comment both.
- [x] T019 Done-only metrics in `Program.cs` (research R4):
  - `GET /exercises/{id}/history` filters `setEntry.Status == SetStatus.Done`.
  - `ProjectExerciseResponses` `LastSet` and `FirstSet` filter done.
  - `SessionCount` counts distinct workouts having at least one done set of the exercise.
  - `GET /workouts` `SetCount` counts done sets, and a new `NotDoneSetCount` counts planned plus skipped.
  - Keep each a single projection (no N+1).
- [x] T020 Add `int NotDoneSetCount` to `backend/GymNotebook.Api/WorkoutSummaryResponse.cs`. Add `status` to `SetEntryResponse` and `notDoneSetCount` to `WorkoutSummaryResponse` in `frontend/src/api/workouts.ts`, with types only and no behaviour change.
- [x] T021 [P] Tests in new `backend/GymNotebook.Tests/SetStatusTests.cs`:
  - `Migration_ExistingSets_ReadAsDone`
  - `PutExercises_OmittedStatusOnInProgressPage_SavesDone`
  - `PutExercises_PlannedOnInProgressPage_Saves`
  - `PutExercises_SkippedOnInProgressPage_Returns400InvalidSetStatus`
  - `PutExercises_PlannedOnFinishedPage_Returns400`
  - `PatchSet_WithoutStatus_KeepsStatus`
  - `PatchWorkout_Finish_TurnsPlannedIntoSkipped`
  - `PatchWorkout_Reopen_TurnsSkippedIntoPlanned`
  - `PutExercises_InvalidStatus_WritesNothing`
- [x] T022 [P] Tests in `backend/GymNotebook.Tests/ExerciseHistoryTests.cs` and `ExerciseTests.cs`:
  - `History_PlannedAndSkippedSets_AreExcluded`
  - `History_AllSkippedPage_AddsNoPoint`
  - `ListExercises_LastSetAndFirstSet_IgnoreNotDoneSets`
  - `ListExercises_SessionCount_CountsOnlyPagesWithDoneSets`
- [x] T023 [P] Tests in `backend/GymNotebook.Tests/WorkoutTests.cs`: `ListWorkouts_SetCounts_SplitDoneAndNotDone`.
- [x] T024 `PLAN.md`: data model (`SetEntry.status`, with the bullet explaining the three values and that only done counts), REST API (status on set routes, finish and reopen transitions, `notDoneSetCount`), and the progress metric section ("non-warmup **done** sets").

**Checkpoint**: `dotnet test` green with every pre-existing test unchanged.

---

## Phase 3: User Story 1: Prepare a session before the gym (Priority: P1) 🎯 MVP

**Goal**: Write a page with exercises and planned sets before training, kept apart from history.

**Independent test**: Prepare two exercises × three sets and reopen on another device. The page is under Prepared with nothing done, and absent from history and progress (spec Story 1).

### Backend (PR 3)

- [x] T030 [US1] `backend/GymNotebook.Api/Workout.cs`: `DateOnly? Date`, `DateTimeOffset? StartedAt`. Fix every compile site in `Program.cs`, `NotebookExport.cs`, `NotebookRestore.cs` and `AccountLifecycle.cs`. Started-only reads use `.Value` after a started filter; comment why.
- [x] T031 [US1] In `backend/GymNotebook.Api/Data/AppDbContext.cs` add the check constraints:
  - `ck_workouts_started_pair`: `(date IS NULL) = (started_at IS NULL)`
  - `ck_workouts_ended_requires_start`: `ended_at IS NULL OR started_at IS NOT NULL`
- [x] T032 [US1] Generate migration `AllowPreparedWorkouts` in `backend/GymNotebook.Api/Migrations/`. Review that it only drops NOT NULL on `date` and `started_at` and adds the two constraints.
- [x] T033 [US1] Make `Date` and `StartedAt` nullable in `backend/GymNotebook.Api/WorkoutDetailResponse.cs`.
- [x] T034 [US1] `GET /workouts` in `Program.cs`:
  - filter `StartedAt != null`
  - a prepared page's id as `before` returns 400, like a foreign cursor
  - ordering unchanged
- [x] T035 [US1] Create `PreparedWorkoutSummaryResponse.cs` and `CreatePreparedWorkoutRequest.cs` (Title?, BodyweightKg?, Location?, Notes?). Map in `Program.cs`:
  - `GET /workouts/prepared`: caller's pages with `StartedAt == null`, `CreatedAt` desc, unpaged, one projection with `exerciseNames`, `exerciseCount`, `setCount`.
  - `POST /workouts/prepared`: same optional-details consent check as `POST /workouts`, 403 `optional_details_consent_required`. Returns 201 `WorkoutDetailResponse` with null date, start and end and revision 1.
- [x] T036 [US1] `PATCH /workouts/{id}` in `Program.cs`: on a prepared page, `date`, `startedAt` or `endedAt` returns 400 `ErrorResponse("not_started")`. Heading fields are allowed as today.
- [x] T037 [P] [US1] Tests in new `backend/GymNotebook.Tests/PreparedWorkoutTests.cs`:
  - `CreatePrepared_Valid_Returns201WithoutDateOrStart`
  - `CreatePrepared_TitleWithoutConsent_Returns403` (privacy factory)
  - `ListPrepared_ReturnsOnlyCallersPreparedNewestFirst`
  - `ListWorkouts_ExcludesPreparedPages`
  - `ListWorkouts_PreparedIdAsBefore_Returns400`
  - `PutExercises_OnPreparedPage_SavesPlanned`
  - `PutExercises_DoneOnPreparedPage_Returns400`
  - `PatchPrepared_StartedAt_Returns400NotStarted`
  - `GetPrepared_OtherUser_Returns404`
  - `DeletePrepared_Owner_Returns204`
  - `ListExercises_PreparedPage_NotCounted`
  - `Database_DateWithoutStart_ViolatesCheck`
- [x] T038 [P] [US1] Tests: in `backend/GymNotebook.Tests/DeletionTests.cs`, `DeleteAccount_WithPreparedPage_RemovesIt`; in `OptionalDetailsConsentTests.cs`, `WithdrawConsent_PreparedPage_ClearsDetails`.
- [x] T039 [US1] `frontend/src/api/workouts.ts`:
  - `date` and `startedAt` become `string | null` in `WorkoutDetailResponse`
  - `listPreparedWorkouts()` and `createPreparedWorkout()`
  - fix the type errors in `frontend/src/screens/` via `npm run typecheck`, without changing behaviour for started pages
- [x] T040 [US1] `PLAN.md`: data model (nullable `date`/`started_at`, the derived three states, both constraints) and REST API (`GET`/`POST /workouts/prepared`, `not_started`).

### Frontend (PR 7, after T003)

- [x] T060 [US1] `frontend/src/screens/newWorkoutDraft.ts`:
  - add `status: 'planned' | 'done' | 'skipped'` to `WorkoutSetDraft`
  - `createExistingWorkoutDraft` reads it, and new rows get `'planned'` in prepare mode and `'done'` otherwise (Q5)
  - `prepareWorkoutDraft` and `prepareAutosaveDraft` send `status`
  - PR 6: the row factories (`createEmptySetDraft`, `createWorkoutExerciseDraft`, `addSetToExercise`) take the new row's status, default `'done'`; prepare mode (T063, PR 7) passes `'planned'`.
- [x] T061 [US1] `frontend/src/screens/editorDraftStorage.ts`: a stored set without `status` reads as `'done'`, so a pre-deploy draft survives. Keep `STORAGE_VERSION = 1` and comment why.
- [x] T062 [P] [US1] Vitest in `frontend/src/screens/newWorkoutDraft.test.ts` and `editorDraftStorage.test.ts`:
  - status defaults per mode
  - status is sent in both payloads
  - a legacy stored row reads as done
- [x] T063 [US1] `frontend/src/screens/NewWorkout.tsx`, prepare mode (`/workouts/prepare`, and `/workouts/:id/edit` when `startedAt === null`):
  - a dateline without a date
  - no done controls, no autosave
  - footer **Save plan** using the strict `prepareWorkoutDraft` path (`createPreparedWorkout`, then `replaceWorkoutExercises`)
  - Cancel and discard as on a finished page
  - layout per the approved mock
  - PR 7: the mock calls these "plans" and the group "Planned"; the code follows it. Save as template (T083) is left to PR 8.
- [x] T064 [US1] Add the `/workouts/prepare` route behind the notice gate in `frontend/src/routes.tsx`.
- [x] T065 [US1] `frontend/src/screens/Sessions.tsx` (+ `Sessions.css`):
  - a Prepared group above the history list from `listPreparedWorkouts()`
  - each row: title or "Untitled plan", exercise summary, set count, opening `/workouts/{id}/edit`
  - a "Prepare a page" entry
  - loading, empty and error states
  - PR 7: "Plan a session" opens the mock's inline chooser with only "A blank page"; its template entries, All templates and the group's Templates link are PR 8 (T084).
- [x] T066 [US1] `frontend/src/screens/WorkoutDetail.tsx`: a prepared page (`startedAt === null`) shows a plan heading instead of a date and no e1RM bests.
- [x] T067 [US1] Update `docs/ui/README.md` "as built" notes for anything that differs from the mock.

**Checkpoint**: Story 1 independent test passes on the local stack.

---

## Phase 4: User Story 2: Tick sets off at the gym (Priority: P1)

**Goal**: Start a prepared page; one tap marks a set done; finishing keeps not-done sets as skipped.

**Independent test**: Start, mark two sets done, close the tab, reopen elsewhere. Those two are done, the page is in progress, and the rest are waiting (spec Story 2).

### Backend (PR 3)

- [x] T041 [US2] `POST /workouts/{id}/start` in `Program.cs` with new `StartWorkoutRequest.cs` (`DateOnly? Date`, `DateTimeOffset? StartedAt`, `int? ExpectedRevision`):
  - 400 `invalid_request` when date or start is missing
  - 404 when not found or not the caller's
  - 409 `ErrorResponse("already_started")` when the page isn't prepared
  - 409 `page_changed` on a stale revision, via `BumpWorkoutRevisionAsync`
  - sets `Date` and `StartedAt` (UTC), keeps sets planned, returns 200 `WorkoutDetailResponse`
- [x] T042 [P] [US2] Tests in `backend/GymNotebook.Tests/PreparedWorkoutTests.cs`:
  - `Start_Prepared_SetsDateAndStartKeepsPlanned`
  - `Start_Twice_Returns409AlreadyStarted`
  - `Start_StaleRevision_Returns409PageChanged`
  - `Start_OtherUser_Returns404`
  - `Start_MissingStartedAt_Returns400`
  - `StartThenFinish_UnmarkedSets_BecomeSkipped`
- [x] T043 [US2] `PLAN.md` REST API: `POST /workouts/{id}/start` and `already_started`.

### Frontend (PR 6 and PR 7, after T003)

- [x] T068 [US2] `startWorkout(id, date, startedAt, expectedRevision)` in `frontend/src/api/workouts.ts`.
- [x] T069 [US2] `frontend/src/screens/newWorkoutDraft.ts`:
  - `findNextSet(draft)`: the first planned set in page order
  - `countNotDone(draft)` for the finish summary
  - Vitest in `newWorkoutDraft.test.ts`
- [x] T070 [US2] **PR 6** `frontend/src/screens/NewWorkout.tsx` (+ `NewWorkout.css`), in-progress mode:
  - a done toggle per set row: 44px, `aria-pressed`, named "Done", distinct from Warm-up, not colour-only
  - toggling triggers an immediate save like a completed row (`saveScheduler`)
  - the **next** marker on `findNextSet`
  - planned rows visually distinct from done
- [x] T071 [US2] **PR 6** `NewWorkout.tsx` finish confirmation: when `countNotDone > 0`, name them ("2 planned sets will be kept as skipped.") before **Confirm finish**.
- [x] T072 [US2] **PR 6** `frontend/src/screens/WorkoutDetail.tsx` (+ `WorkoutDetail.css`):
  - skipped sets get a "skipped" tag and are demoted like warm-ups
  - in progress, planned sets show as to do
  - the block best (`frontend/src/screens/workoutFormat.ts`) ignores non-done sets, with Vitest in `workoutFormat.test.ts`
- [x] T073 [US2] **PR 7** **Start session** in prepare mode in `NewWorkout.tsx`: save first, then `startWorkout` with the local date and now, then switch to in-progress mode with autosave. Also a **Start** on each Prepared row in `frontend/src/screens/Sessions.tsx`.
- [x] T074 [US2] **PR 6** `frontend/src/screens/Sessions.tsx`: the meta line uses `notDoneSetCount` per the mock ("9 sets · 2 skipped" / "4 of 9 done").
- [x] T075 [US2] `docs/ui/README.md`: done toggle, next set, finish summary, skipped display (as built).

**Checkpoint**: Story 1 + Story 2 form the MVP and match the issue's use case.

---

## Phase 5: User Story 3: Adjust the plan during the session (Priority: P2)

**Goal**: Change weight or reps before or after done, add or remove exercises and sets, and fix forgotten taps on a finished page.

**Independent test**: Change one planned weight and another's reps, mark both done, add an exercise with a done set. All three are recorded with the changed values (spec Story 3).

- [x] T044 [P] [US3] Backend tests in `backend/GymNotebook.Tests/SetStatusTests.cs`:
  - `PutExercises_ChangedValuesOnDoneSet_KeepsDone`
  - `PutExercises_SkippedToDoneOnFinishedPage_Saves` (FR-010a)
  - `PutExercises_RemoveSkippedSetOnFinishedPage_Saves`
- [x] T076 [US3] `frontend/src/screens/NewWorkout.tsx`: weight and reps stay editable on done rows without clearing done; newly added rows and blocks start done (Q5).
- [x] T077 [US3] `NewWorkout.tsx`, finished-page edit: skipped rows show the done toggle (skipped ↔ done) and can be removed with the existing undo.
- [x] T078 [P] [US3] Vitest in `frontend/src/screens/newWorkoutDraft.test.ts`: editing a done row keeps its status; "Add set" copies figures but starts `'done'`; toggling skipped gives done.

**Checkpoint**: Stories 1–3 work independently of templates.

---

## Phase 6: User Story 4: Keep my routines as named templates (Priority: P3)

**Goal**: Named per-user templates, prepare from one, save a page as one; copies never linked.

**Independent test**: Create "Push A", prepare from it, change a weight. The template keeps the old weight (spec Story 4).

### Backend (PR 4)

- [x] T045 [P] [US4] Create entities `backend/GymNotebook.Api/Template.cs` (Id, UserId, `required Name`, `required NormalizedName`, CreatedAt, `ICollection<TemplateExercise> Exercises`), `TemplateExercise.cs` (Id, TemplateId, ExerciseId, Position, `ICollection<TemplateSet> Sets`) and `TemplateSet.cs` (Id, TemplateExerciseId, SetNumber, `decimal? Weight`, `int Reps`, `bool IsWarmup`). No upward navigation properties.
- [x] T046 [US4] Configure in `backend/GymNotebook.Api/Data/AppDbContext.cs` (`DbSet`s plus model config):
  - `templates`: FK user cascade, **unique `ix_templates_user_id_normalized_name` on `(user_id, normalized_name)`**, `created_at` default `now()`
  - `template_exercises`: FK template cascade, FK `exercise_id` **Restrict**, index on `template_id`
  - `template_sets`: FK cascade, `weight numeric(6,2)`, index on `(template_exercise_id, set_number)`
- [x] T047 [US4] Generate migration `AddTemplates` in `backend/GymNotebook.Api/Migrations/`. Review that it holds exactly three tables, their FKs and indexes.
- [x] T048 [P] [US4] Create DTOs, one per file in `backend/GymNotebook.Api/`:
  - `TemplateSummaryResponse` (Id, Name, ExerciseNames, ExerciseCount, SetCount)
  - `TemplateDetailResponse`, `TemplateExerciseResponse`, `TemplateSetResponse`
  - `SaveTemplateRequest` (Name, `List<PutWorkoutExerciseInput> Exercises`)
  - `SaveAsTemplateRequest` (Name)
- [x] T049 [US4] Map a `/templates` group in `Program.cs` with `.RequireAuthorization().RequireAccountLifecycle()`:
  - `GET /` ordered by Name, one projection
  - `GET /{id:int}`
  - `POST /`
  - `PUT /{id:int}`: replace blocks in one transaction like `PUT /workouts/{id}/exercises`
  - `DELETE /{id:int}` → 204
  - names normalized with `ExerciseNameNormalizer.Normalize`; blank name, blank exercise name or reps < 1 → 400 `invalid_request`
  - exercises via `GetOrCreateExerciseAsync`
  - `catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ix_templates_user_id_normalized_name"))` → 409 `ErrorResponse("template_name_taken")`, writing nothing
  - foreign or missing id → 404
- [x] T050 [US4] `POST /templates/{id:int}/prepare` in `Program.cs`:
  - a new prepared page copying the blocks and sets as `planned`
  - title = template name, or null when the privacy feature is on and the account lacks optional-details consent (reuse the check from `OptionalDetails.cs`)
  - 201 `WorkoutDetailResponse`
- [x] T051 [US4] `POST /workouts/{id:int}/template` in `Program.cs`: copies every block and set (weight, reps, warm-up) whatever its status; 201 `TemplateDetailResponse`; 409 `template_name_taken`; 404 for a foreign page.
- [x] T052 [US4] `PATCH /exercises/{id}` merge in `Program.cs`: re-point the losing exercise's `TemplateExercises` along with its `WorkoutExercises`, in the same `SaveChanges` (research R7).
- [x] T053 [US4] `backend/GymNotebook.Api/AccountDeletion.cs`: `Templates.Where(UserId).ExecuteDeleteAsync` before the exercises delete. Comment the Restrict FK reason.
- [x] T054 [P] [US4] Tests in new `backend/GymNotebook.Tests/TemplateTests.cs`:
  - `CreateTemplate_Valid_Returns201`
  - `CreateTemplate_NameDiffersOnlyByCaseAndSpaces_Returns409AndWritesNothing`
  - `CreateTemplate_SameNameOtherUser_Returns201`
  - `RenameTemplate_OntoTakenName_Returns409`
  - `CreateTemplate_BlankName_Returns400`
  - `ListTemplates_OrderedByName_OnlyCallers`
  - `GetTemplate_OtherUser_Returns404`
  - `PutTemplate_ReplacesBlocks`
  - `DeleteTemplate_LeavesPreparedPages`
  - `Prepare_CopiesBlocksAsPlanned`
  - `Prepare_ThenEditPage_TemplateUnchanged`
  - `EditTemplate_AfterPrepare_PageUnchanged`
  - `Prepare_WithoutConsent_HasNoTitle` (privacy factory): built as `Prepare_FromTemplate_TitleFollowsConsent` in `OptionalDetailsConsentTests.cs`, which already runs the privacy factory, covering both with and without consent
  - `SaveAsTemplate_FinishedPageWithSkipped_CopiesAllSets`
  - `SaveAsTemplate_OtherUsersPage_Returns404`
  - `MergeExercise_UsedInTemplate_RepointsTemplateBlocks`
  - `Templates_NeverAffectExerciseSessionCount`
- [x] T055 [P] [US4] Tests: `DeleteAccount_WithTemplates_RemovesThem` in `backend/GymNotebook.Tests/DeletionTests.cs`; extend `LifecycleCoverageTests.cs` so the `/templates` routes are covered by the lifecycle filter.
- [x] T056 [US4] `PLAN.md`: data model (the three tables, unique name per user, normalized like exercises, refused rather than merged) and REST API (`/templates` routes, prepare, save-as-template, merge re-pointing).

### Frontend (PR 8, after T003)

- [ ] T079 [P] [US4] Create `frontend/src/api/templates.ts`: types for the template DTOs; `listTemplates`, `getTemplate`, `createTemplate`, `updateTemplate`, `deleteTemplate`, `prepareFromTemplate`, `saveWorkoutAsTemplate`; constant `TEMPLATE_NAME_TAKEN = 'template_name_taken'`.
- [ ] T080 [US4] Create `frontend/src/screens/Templates.tsx` (+ `Templates.css`): list ordered by name, loading/empty/error, **New template**, and **Prepare a session** per row.
- [ ] T081 [US4] Create `frontend/src/screens/EditTemplate.tsx`:
  - name field, blocks and set rows reusing the `newWorkoutDraft.ts` helpers and exercise autocomplete, no status
  - explicit **Save**
  - inline delete confirm ("Delete template Push A? Sessions made from it stay.")
  - a 409 shows "You already have a template called Push A." at the name field with `aria-invalid`, keeping input
  - extract shared block and row pieces from `NewWorkout.tsx` only as far as needed (plan D7)
- [ ] T082 [US4] `frontend/src/routes.tsx`: `/templates`, `/templates/new` and `/templates/:templateId` behind the notice gate.
- [ ] T083 [US4] **Save as template** on `frontend/src/screens/WorkoutDetail.tsx` and in prepare mode of `NewWorkout.tsx`; name prefilled from the title.
- [ ] T084 [US4] The SC-007 entry from Sessions (`frontend/src/screens/Sessions.tsx`): "Prepare from template" reaches a prepared page in at most three taps from the cover.
- [ ] T085 [US4] `docs/ui/README.md` Templates section, as built.

**Checkpoint**: All four stories independently testable.

---

## Phase 7: Polish & cross-cutting: backup v2 (PR 5) and wrap-up

- [ ] T057 `backend/GymNotebook.Api/NotebookExport.cs`:
  - `formatVersion: 2`
  - nullable `date`/`startedAt`
  - `sets[].status`
  - new keyset-batched arrays `templates` (id, userId, name, createdAt), `templateExercises` (id, templateId, exerciseId, position) and `templateSets` (id, templateExerciseId, setNumber, weight, reps, isWarmup)
- [ ] T058 `backend/GymNotebook.Api/ExportFieldGuide.cs`: an entry for every new field.
- [ ] T059 `backend/GymNotebook.Api/NotebookRestore.cs`:
  - accept versions 1 and 2; v1 sets restore as `done`
  - prepared pages (null `startedAt`) are always added (Q2), with validation that date and end are also null
  - set status must fit the page state (`SetStatusRules`)
  - template names unique within the file after normalizing; existing names skipped
  - template exercise refs must resolve or the file is `broken_reference`
  - result gains `PreparedPagesAdded`, `TemplatesAdded`, `TemplatesAlreadyPresent`
  - optional details dropped on prepared pages without consent
- [ ] T086 [P] Tests in `backend/GymNotebook.Tests/ExportTests.cs` and `RestoreTests.cs`:
  - `Export_V2_IncludesStatusPreparedPagesAndTemplates`
  - `Restore_V2RoundTrip_RecreatesPreparedSkippedAndTemplates`
  - `Restore_V1File_SetsRestoreAsDone`
  - `Restore_PreparedTwice_AddsTwice`
  - `Restore_ExistingTemplateName_KeepsAccountsTemplate`
  - `Restore_PreparedPageWithStartedAt_Returns400BackupInvalid`
  - `Restore_DoneSetOnPreparedPage_Returns400`
  - `Restore_DuplicateTemplateNamesInFile_Returns400`
  - `Restore_TemplateBrokenExerciseRef_Returns400`
- [ ] T087 `frontend/src/screens/backupCsv.ts` and `frontend/src/api/backup.ts`: v2 types, a `status` column in the sets CSV, three template CSVs, and the new restore result counts in `frontend/src/screens/Backup.tsx`. Vitest in `backupCsv.test.ts`.
- [ ] T088 Mark the format version 2 change in `specs/001-privacy-account-lifecycle/` and `specs/004-backup-restore/` export contracts, and in `PLAN.md` → backup.
- [ ] T089 `PLAN.md` → Milestones: add milestone 15 "Predefined sessions" with the PR list and links to `specs/006-predefined-sessions/`.
- [ ] T090 Run all checks in [quickstart.md](quickstart.md) (`dotnet test`, `dotnet format --verify-no-changes`, frontend typecheck, lint, format:check and test) before each PR.
- [ ] T091 Owner phone walkthrough (quickstart steps 1–6), covering SC-001, SC-002, SC-003, SC-007 and SC-008, on the local stack and after deploy.

---

## Dependencies & execution order

- **T003 (mock)** gates every frontend task (T060–T085, T087's UI part). It can run alongside the backend PRs.
- **Phase 2 (PR 2)** blocks Phases 3–6.
- **US1 backend (PR 3)** and **US2 backend** share PR 3: T030–T043.
- **US4 backend (PR 4)** needs PR 2 and PR 3, since `prepare` creates prepared pages.
- **Backup (PR 5)** needs PRs 2–4, since it exports everything.
- **Frontend PRs**:
  - PR 6 (T060–T062, T069–T072, T074–T078) needs PR 2 and T003.
  - PR 7 (T063–T068, T073) needs PR 3 and PR 6.
  - PR 8 (T079–T085) needs PR 4 and PR 7.
- **Within a phase**: entity and config before migration, migration before routes, routes before tests that call them. Tests marked [P] touch separate files.

### Parallel opportunities

- T010, T011 in parallel. T021, T022, T023 in parallel after T019.
- T037 and T038 in parallel after T036.
- T045 and T048 in parallel. T054 and T055 in parallel after T053.
- The UI mock (T003) in parallel with PRs 2–5.
- T079 in parallel with T080–T081 scaffolding once the types are agreed.

## Implementation strategy

**MVP = US1 + US2** (prepare, start, tick off, finish with skipped): PRs 2, 3, 6 and 7 plus the mock. That alone delivers issue #165's use case. US3 mostly comes free from the existing editor plus T076–T077. US4 (templates) and backup v2 follow as their own PRs. The deploy order keeps backup v2 (PR 5) **before** the frontend can create prepared pages or templates in production. That way no production export runs as version 1 while prepared pages or skipped sets exist. **Do not deploy PR 7 or PR 8 to production before PR 5 has merged.**
