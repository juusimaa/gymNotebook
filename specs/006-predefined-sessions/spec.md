# Feature Specification: Predefined Sessions

**Feature Branch**: `006-predefined-sessions`

**Created**: 2026-10-15

**Status**: Draft for owner review. Nothing here is approved for implementation until the owner merges this plan (Principle VII). The UI mock (made with `/impeccable`) must also be approved before implementation, per issue #165.

**Input**: GitHub issue #165, "Predefined sessions": "User wants to define exercises before going to gym so that only thing to do in gym is to mark set as done. It should be also possible to add new exercises to predefined sets, change weight and rep count."

## Clarifications

### Session 2026-10-15

- Q: What happens to planned sets never marked done when a session is finished? → A: They stay on the page marked "skipped", so plan versus actual stays visible; they never count as training.
- Q: Is reusing sessions in scope, and in what form? → A: Yes, as named reusable templates kept apart from sessions (Story 4).
- Q: May two templates share a name? → A: No. Names are unique per user, normalized like exercise names; a clash is refused, never merged.
- Planning questions (plan.md Q1–Q5), accepted as proposed: reopening a finished page turns skipped sets back to planned; restore always adds prepared pages; a saved planned set needs reps; templates are last-write-wins; rows added during a session start done.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Prepare a session before the gym (Priority: P1)

As a lifter at home the evening before, I write out tomorrow's session: the exercises in order, and for each one the sets I intend to do with their weight and reps. Nothing is logged as done yet; it is a plan on a page.

**Why this priority**: Without a prepared session there is nothing to tick off at the gym. This is the half of the feature that moves typing out of the gym.

**Independent Test**: Prepare a session with two exercises and three sets each, leave the app, come back on another device: the prepared session is there with every planned set, none marked done, and it does not appear in the logged history or on any progress chart.

**Acceptance Scenarios**:

1. **Given** I'm signed in, **When** I choose to prepare a session, **Then** I can add exercises (with the same exercise suggestions as when logging) and planned sets with weight, reps and warm-up flag, and keep it without starting it.
2. **Given** a prepared session, **When** I open my notebook, **Then** I can find it separately from logged sessions, with its title and planned exercises visible.
3. **Given** a prepared session, **When** I reopen it before the gym, **Then** I can still add, remove, reorder and change exercises and sets.
4. **Given** a prepared session nobody has started, **When** I view an exercise's progress, **Then** its planned sets do not appear.
5. **Given** a prepared session I no longer want, **When** I delete it, **Then** it is gone after confirmation, and no logged history changes.

---

### User Story 2 - Tick sets off at the gym (Priority: P1)

As a lifter at the rack, I open my prepared session, start it, and after each set I tap once to mark it done. That is all I have to do when the set went as planned.

**Why this priority**: This is the outcome the issue asks for: the only action at the gym is marking a set done. Together with Story 1 it is the MVP.

**Independent Test**: Start a prepared session, mark two of its sets done with one tap each, close the tab, reopen on another device: those two sets are recorded as done with their planned weight and reps, the session is in progress, and the rest are still waiting.

**Acceptance Scenarios**:

1. **Given** a prepared session, **When** I start it, **Then** it becomes an in-progress session dated and timed when I started, not when I prepared it.
2. **Given** an in-progress session with planned sets, **When** I tap a set's done control, **Then** the set is recorded as done with its planned weight and reps, and it is saved like any other logged set (within seconds, without a Save button).
3. **Given** a set marked done by mistake, **When** I tap it again, **Then** it returns to not done.
4. **Given** an in-progress session, **When** I look at it, **Then** I can tell at a glance which sets are done and which are still to do, and which set is next.
5. **Given** I finish the session, **When** some planned sets were never marked done, **Then** they stay on the page marked "skipped", with their planned weight and reps, so I can later see plan versus actual.
6. **Given** a finished session with skipped sets, **When** I correct it with **Edit**, **Then** I can mark a skipped set done (I forgot to tap it) or remove it.

---

### User Story 3 - Adjust the plan during the session (Priority: P2)

As a lifter mid-session, the bar felt heavy so I take 2.5 kg off, or I managed one more rep, or I add an exercise the plan didn't have. I change the numbers on the set before or as I mark it done, and add exercises or sets as I would on any page.

**Why this priority**: Plans rarely survive the gym unchanged; the issue names changing weight and reps and adding exercises explicitly. A plan that can't be adjusted would push the user back to logging by hand.

**Independent Test**: In a started prepared session, change one planned set's weight and another's reps, mark both done, add a new exercise with one set and mark it done: all three are recorded as done with the changed values.

**Acceptance Scenarios**:

1. **Given** a planned set, **When** I change its weight or reps and mark it done, **Then** the done set records the changed values.
2. **Given** a set already marked done, **When** I correct its weight or reps, **Then** the correction is saved and the set stays done.
3. **Given** an in-progress session, **When** I add an exercise or a set, **Then** it is added like on any page, and can be marked done the same way.
4. **Given** an in-progress session, **When** I remove a planned set or exercise I'm skipping, **Then** it is removed from the page.
5. **Given** I adjusted the plan during the session, **When** I finish, **Then** the logged session reflects what I actually did; the plan I started from is not kept as a separate record.

---

### User Story 4 - Keep my routines as named templates (Priority: P3)

As a lifter following a routine, I don't want to type the same "Push A" every week. I keep named templates ("Push A", "Legs B") with their exercises and sets, and prepare a session from one, changing only what differs this time.

**Why this priority**: Saves the most typing for routine lifters, but Story 1 already delivers the core value without it.

**Independent Test**: Create a template "Push A" with three exercises, prepare a session from it, change one weight in the session: the prepared session has the template's exercises and sets in order, none done, with the changed weight; the template still has the original weight.

**Acceptance Scenarios**:

1. **Given** I'm signed in, **When** I open my templates, **Then** I can create, rename, edit and delete named templates, each holding ordered exercises with planned sets (weight, reps, warm-up flag).
2. **Given** a template, **When** I prepare a session from it, **Then** a new prepared session holds a copy of its name (as the title), exercises and sets, all not done.
3. **Given** a session prepared from a template, **When** I change the session, **Then** the template is untouched; **When** I change the template, **Then** sessions already prepared or logged from it are untouched.
4. **Given** an earlier logged or prepared session, **When** I choose to save it as a template, **Then** a new template holds its exercises and sets with the values as they were (done state not copied), named from the session title by default.
5. **Given** a template I delete, **When** sessions were prepared from it, **Then** those sessions remain as they are.
6. **Given** my templates, **When** I look at history, progress charts or session counts, **Then** templates never appear there.

### Edge Cases

- Two prepared sessions exist at once (e.g. Monday and Wednesday planned on Sunday): both are kept, and I choose which to start.
- A prepared session is started on another device at the same time: the existing stale-save protection applies; the second device is told the page changed and offered a reload.
- A planned set has no weight (bodyweight exercise) or only added weight: marking it done records it like a logged bodyweight set.
- A planned set has incomplete values (weight but no reps): it stays in the editor's draft, as an incomplete row does today, and the plan can't be saved until it is complete or removed. *Narrowed in planning (research R12): a saved planned set always has reps.*
- Every planned set gets removed during the session and nothing is marked done: finishing behaves as for an empty page today (the user is offered to tear it out).
- An exercise used in a prepared session is renamed or merged: the prepared session follows the rename, as logged sessions do.
- Deleting an exercise that appears only in prepared sessions: follows the same rule as for logged sets (refused while sets reference it), unless planning decides otherwise.
- Backup and restore: prepared sessions are either included and restored as prepared, or excluded; they must never be restored as logged history.
- Another user's prepared session or template is requested: it is reported as not found, exactly like another user's workout.
- A finished session where every set was skipped: it stays as a finished page showing only skipped sets; it adds nothing to progress charts and does not count as a session in which those exercises were trained.
- A template with no exercises: it can be saved (a name to fill in later), and preparing from it gives an empty prepared session.
- Two templates with the same name: refused. Names are unique per user and compared like exercise names (ignoring case and extra whitespace), so "Push A" and " push a " clash; different users may each have a "Push A". Creating, renaming or saving a session as a template onto a taken name tells the user the name is in use and keeps their input; nothing is merged or overwritten.
- An exercise used only in templates: deleting it follows the same rule as for sets in sessions (refused while referenced), and a rename or merge carries through to templates.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Users MUST be able to create a prepared session with optional heading fields (title and the other existing optional heading fields, subject to the existing optional-details consent) and an ordered list of exercises, each with ordered planned sets carrying weight, reps and warm-up flag.
- **FR-002**: Exercises in a prepared session MUST be chosen and created the same way as when logging (same suggestions, same bodyweight handling, same name normalization).
- **FR-003**: Users MUST be able to edit and delete a prepared session before starting it.
- **FR-004**: Prepared sessions MUST be listed apart from logged sessions, and MUST NOT count as sessions in history counts, exercise session counts, "last time" hints, or progress charts until started.
- **FR-005**: Users MUST be able to start a prepared session; starting sets its date and start time to the moment of starting and makes it an in-progress session with the existing autosave behavior.
- **FR-006**: In an in-progress session, every set MUST have a done state that the user can toggle with a single tap.
- **FR-007**: Only sets marked done MUST count as logged training (progress chart, "last time" hints, best-set calculations, exports labelled as training data).
- **FR-008**: Users MUST be able to change a set's weight and reps before or after marking it done, and add or remove exercises and sets during the session.
- **FR-009**: The in-progress view MUST make done, not-done and next sets visually distinguishable.
- **FR-010**: Finishing a session MUST keep every not-done set on the page as "skipped", with its planned values, and MUST never silently turn a not-done set into a done one. Skipped sets MUST be visually distinct from done sets wherever a finished page is shown, and MUST NOT count as logged training (FR-007).
- **FR-010a**: When correcting a finished page, users MUST be able to mark a skipped set done or remove it.
- **FR-011**: Sets logged on a page that was not prepared (the existing "new page" flow) MUST keep working as today; a set entered directly is recorded as done, so existing logging is not slowed by an extra tap.
- **FR-012**: Existing logged sessions MUST be treated as fully done after this feature ships; no history changes meaning.
- **FR-013**: Prepared sessions MUST be scoped to their owner with the same not-found behavior as workouts, and MUST be removed with the account.
- **FR-014**: Backup export and restore MUST handle prepared sessions and the done state so that a restore never turns planned sets into logged ones.
- **FR-015**: Prepared sessions and templates MUST be available on any device the user signs in on (stored with the account, not only in the browser).
- **FR-016**: Users MUST be able to create, rename, edit and delete named templates of ordered exercises with planned sets. A template name MUST be non-empty and unique per user, compared the same way as exercise names (case and whitespace insensitive), while keeping the casing the user typed for display.
- **FR-017**: Users MUST be able to prepare a session from a template and to save an existing session as a new template; both are copies, and later changes to one never affect the other.
- **FR-018**: Templates MUST be scoped to their owner with the same not-found behavior as workouts, MUST be removed with the account, and MUST be included in backup export and restored as templates.
- **FR-019**: Templates MUST NOT appear in history, session counts, "last time" hints or progress charts.

### Key Entities *(include if feature involves data)*

- **Prepared session**: a notebook page written before training. Has the same heading fields and exercise blocks as a logged page, but no actual date/start time until started. Becomes an ordinary in-progress page when started. Whether it is a state of the existing page or a separate record is a planning decision.
- **Planned set**: a set row with intended weight, reps and warm-up flag, plus a state: planned (not done yet), done, or skipped (session finished without it being done). Only done sets are training history.
- **Set (existing)**: gains that state. Existing sets are done.
- **Template**: a named, reusable routine owned by one user, its name unique within that user's templates: an ordered list of exercises, each with planned sets (weight, reps, warm-up flag). It has no date, no done state and no heading fields beyond its name. It is copied into a prepared session, never linked to one.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: At the gym, a set performed as planned takes exactly one tap to record, with no typing.
- **SC-002**: A user can prepare a session with 5 exercises and 15 sets in under 5 minutes on a phone.
- **SC-003**: Changing a planned set's weight or reps during the session takes no more taps than editing a set does today.
- **SC-004**: Zero planned-but-not-done sets appear on progress charts, "last time" hints or logged-session counts, verified across preparing, starting, finishing and backup/restore.
- **SC-005**: A set marked done survives closing the tab or losing the phone, and is visible on another device within seconds, as logged sets do today.
- **SC-006**: All existing sessions and charts look the same after the feature ships as before it.
- **SC-007**: Preparing a session from a template takes at most three taps from the notebook cover, with no typing.
- **SC-008**: On a finished page, every skipped set is identifiable as skipped without opening it, and none is counted on any progress chart.

## Assumptions

- "Predefined session" means a prepared notebook page: written before training, ticked off during it. The prepared page and the logged page are the same page at different moments, matching the notebook model in `PLAN.md`.
- Several prepared sessions may exist at once; there is no scheduling or calendar date on a prepared session beyond an optional title.
- Templates hold no optional heading details (bodyweight, location, notes), so they need no optional-details consent; the template name is the user's routine label, copied into the session title only when the user has that consent, as for any title.
- Templates do not track progression (e.g. "add 2.5 kg each week"); weights are whatever the user last saved in the template.
- The done state applies only to sets; there is no separate "exercise done" state (an exercise is done when its sets are).
- Starting a prepared session reuses milestone 13's durable logging: autosave, stale-save protection, renewing sign-in. Before starting, a prepared session is edited with an explicit save like a finished page, unless planning finds autosave simpler.
- Rest timers, progression rules (auto-increasing weight), and reminders are out of scope.
- Phone-first, same visual language and tokens as the existing UI; a UI mock must be approved before implementation (issue #165).
- No new user roles or sharing: prepared sessions are private to their owner, like everything else.
