import type { ExerciseResponse } from '../api/exercises'
import type {
  CreateWorkoutRequest,
  PutWorkoutExercisesRequest,
  WorkoutDetailResponse,
} from '../api/workouts'
import { normalizeExerciseName } from './exerciseFormat'
import { formatWorkoutDate } from './workoutFormat'

// Form fields stay as strings so empty and partially entered values such as
// "78." remain representable until the draft is validated for submission.
export interface WorkoutHeadingDraft {
  date: string
  startTime: string
  title: string
  bodyweightKg: string
  location: string
  notes: string
}

function padTwoDigits(value: number): string {
  return value.toString().padStart(2, '0')
}

function formatLocalTime(value: string): string {
  const date = new Date(value)
  return `${padTwoDigits(date.getHours())}:${padTwoDigits(date.getMinutes())}`
}

// Uses local date/time getters because the workout date is the calendar date
// chosen by the user; converting through UTC could shift it to another day.
export function createInitialHeadingDraft(now: Date): WorkoutHeadingDraft {
  const year = now.getFullYear()
  const month = padTwoDigits(now.getMonth() + 1)
  const day = padTwoDigits(now.getDate())
  const hours = padTwoDigits(now.getHours())
  const minutes = padTwoDigits(now.getMinutes())

  return {
    date: `${year}-${month}-${day}`,
    startTime: `${hours}:${minutes}`,
    title: '',
    bodyweightKg: '',
    location: '',
    notes: '',
  }
}

// specs/001 user story 6: title, bodyweight, location and notes are the
// "optional details" an account may store only with consent. Without it the
// editor hides their inputs and saves the draft without them, so a hidden
// value can never be sent. A new object: the caller's draft isn't changed.
export function dropOptionalDetails(
  heading: WorkoutHeadingDraft,
): WorkoutHeadingDraft {
  return { ...heading, title: '', bodyweightKg: '', location: '', notes: '' }
}

// Whether any of the four holds something that would be saved. Blank text
// counts as empty, matching what prepareWorkoutDraft sends as null.
export function hasOptionalDetails(heading: WorkoutHeadingDraft): boolean {
  return [
    heading.title,
    heading.bodyweightKg,
    heading.location,
    heading.notes,
  ].some((value) => value.trim() !== '')
}

// The editor's page heading collapses to one line so the exercises come
// first: "Today 09.34", "3 Oct 09.34–10.40", or with the year when it isn't
// this year's page. Times follow the HH.mm convention (spec rule 11). Reads
// the draft's own strings, so a half-typed field shows as it stands rather
// than throwing; validation still happens at save.
export function describeHeadingWhen(
  heading: Pick<WorkoutHeadingDraft, 'date' | 'startTime'>,
  endTime: string,
  now: Date,
): string {
  const [year, month, day] = heading.date.split('-').map(Number)
  const hasDate =
    Number.isInteger(year) &&
    Number.isInteger(month) &&
    Number.isInteger(day) &&
    month >= 1 &&
    month <= 12

  let date = 'No date'
  if (hasDate) {
    const isToday =
      year === now.getFullYear() &&
      month === now.getMonth() + 1 &&
      day === now.getDate()
    date = isToday
      ? 'Today'
      : `${day} ${formatWorkoutDate(heading.date).month}${year === now.getFullYear() ? '' : ` ${year}`}`
  }

  const start = heading.startTime.replace(':', '.')
  const end = endTime.replace(':', '.')
  if (start === '') {
    return date
  }
  return end === '' ? `${date} ${start}` : `${date} ${start}–${end}`
}

export interface ExistingWorkoutDraft {
  heading: WorkoutHeadingDraft
  endTime: string
  exercises: WorkoutExerciseDraft[]
}

// Rehydrates the server response into the same string-valued draft used by the
// new-page editor. The id factory keeps React keys local and makes this conversion
// deterministic in tests.
export function createExistingWorkoutDraft(
  workout: WorkoutDetailResponse,
  createClientId: () => string,
): ExistingWorkoutDraft {
  return {
    heading: {
      date: workout.date,
      startTime: formatLocalTime(workout.startedAt),
      title: workout.title ?? '',
      bodyweightKg:
        workout.bodyweightKg === null ? '' : String(workout.bodyweightKg),
      location: workout.location ?? '',
      notes: workout.notes ?? '',
    },
    endTime: workout.endedAt === null ? '' : formatLocalTime(workout.endedAt),
    exercises: workout.exercises.map((exercise) => ({
      clientId: createClientId(),
      exerciseId: exercise.exerciseId,
      exerciseName: exercise.exerciseName,
      isBodyweight: exercise.isBodyweight,
      isAddedWeightEnabled:
        exercise.isBodyweight &&
        exercise.sets.some((set) => set.weight !== null),
      sets: exercise.sets.map((set) => ({
        clientId: createClientId(),
        weight: set.weight === null ? '' : String(set.weight),
        reps: String(set.reps),
        isWarmup: set.isWarmup,
      })),
    })),
  }
}

// Set input values stay as strings while editing so empty and partial values
// remain representable. clientId is only a stable React key and is never sent
// to the API.
export interface WorkoutSetDraft {
  clientId: string
  weight: string
  reps: string
  isWarmup: boolean
}

// A set update can change only editable values. Excluding clientId keeps the
// stable React key intact when a field is edited.
export type WorkoutSetDraftChanges = Partial<
  Pick<WorkoutSetDraft, 'weight' | 'reps' | 'isWarmup'>
>

export function createEmptySetDraft(clientId: string): WorkoutSetDraft {
  return {
    clientId,
    weight: '',
    reps: '',
    isWarmup: false,
  }
}

// Repeated exercise names are valid because each appearance is a separate block.
// clientId is a frontend-only React key; exerciseId is null for a new exercise
// that the backend will get-or-create when the workout is saved.
export interface WorkoutExerciseDraft {
  clientId: string
  exerciseId: number | null
  exerciseName: string
  isBodyweight: boolean
  // UI-only switch controlling the added-weight field for bodyweight exercises.
  isAddedWeightEnabled: boolean
  // The exercise's most recent set before this page, shown as "last time" in
  // the block: on a new page, from the suggestion it was picked from; on the
  // edit page of a session still in progress, from that or, for blocks already
  // on the page, from withLastTime. The edit page asks the server to leave the
  // page itself out, so the hint is never one of its own sets.
  // Optional so drafts stored before it existed still restore. Never sent.
  lastSet?: ExerciseResponse['lastSet']
  sets: WorkoutSetDraft[]
}

// Continuing a session in progress loads its blocks from the server, which
// knows nothing of "last time": only a block picked in this visit had one.
// So after a Save page and Continue logging (the phone locked between sets,
// say), the reference vanished exactly when it was needed. This fills it in
// from one GET /exercises answer asked with excludeWorkoutId, so it is the
// session before this page. A block that already has a lastSet keeps it, and
// a new exercise (no id yet) has no history to show.
export function withLastTime(
  exercises: WorkoutExerciseDraft[],
  found: Pick<ExerciseResponse, 'id' | 'lastSet'>[],
): WorkoutExerciseDraft[] {
  const lastSetById = new Map(
    found.map((exercise) => [exercise.id, exercise.lastSet]),
  )
  return exercises.map((exercise) =>
    exercise.lastSet !== undefined || exercise.exerciseId === null
      ? exercise
      : { ...exercise, lastSet: lastSetById.get(exercise.exerciseId) ?? null },
  )
}

// The ditto mark across sessions: a block picked with a "last time" set
// starts with that set written in, weight, reps and warm-up flag, because
// "write what you did last time, then adjust" is how a paper log is filled
// in. The figure is a draft like any other — the editor selects it on focus,
// so a different number is simply typed over it.
function createSetDraftFromLastSet(
  clientId: string,
  lastSet: NonNullable<ExerciseResponse['lastSet']>,
): WorkoutSetDraft {
  return {
    clientId,
    weight: lastSet.weight === null ? '' : String(lastSet.weight),
    reps: String(lastSet.reps),
    // Absent on a hint restored from a draft stored before the flag existed.
    isWarmup: lastSet.isWarmup === true,
  }
}

// A new exercise block always begins with one editable set copied from last
// time, when there is one, otherwise empty. Which of last time's sets: the
// warm-up it opened with (firstSet), if it opened with one, so this session
// starts the same way — "+ Add set" then repeats its figures as a working
// set; otherwise its final working set (lastSet). Both client
// IDs are supplied by the caller so this factory stays deterministic and easy
// to test.
export function createWorkoutExerciseDraft(
  clientId: string,
  initialSetClientId: string,
  exerciseId: number | null,
  exerciseName: string,
  isBodyweight: boolean,
  lastSet: ExerciseResponse['lastSet'] = null,
  firstSet: ExerciseResponse['firstSet'] = null,
): WorkoutExerciseDraft {
  const copiedSet = firstSet?.isWarmup === true ? firstSet : lastSet

  return {
    clientId,
    exerciseId,
    exerciseName,
    isBodyweight,
    // A bodyweight exercise last done with added weight opens with the weight
    // field showing, so the copied added weight is visible and editable — and
    // never sits in a hidden field that the save would quietly drop.
    isAddedWeightEnabled:
      isBodyweight &&
      [lastSet, copiedSet].some((set) => set !== null && set.weight !== null),
    lastSet,
    sets: [
      copiedSet === null
        ? createEmptySetDraft(initialSetClientId)
        : createSetDraftFromLastSet(initialSetClientId, copiedSet),
    ],
  }
}

// "+ Add set" is the notebook's ditto mark (design-fix-plan step 3): the new
// set repeats the figures above it, so a repeated set costs one tap instead
// of retyping both numbers. It always starts as a working set, though: copying
// the warm-up flag meant the first heavy set after the warm-ups was silently
// logged as one more warm-up and dropped out of progress, with nothing on
// screen to say so. Pressing Warm-up again is visible; a missing working set
// is not. The first set of a block has nothing above it and starts empty.
// Returns a new block and set array so React can observe the change; the
// original stays untouched.
export function addSetToExercise(
  exercise: WorkoutExerciseDraft,
  setClientId: string,
): WorkoutExerciseDraft {
  const previous = exercise.sets.at(-1)
  const set =
    previous === undefined
      ? createEmptySetDraft(setClientId)
      : { ...previous, clientId: setClientId, isWarmup: false }
  return { ...exercise, sets: [...exercise.sets, set] }
}

// Undo for a removed set: puts it back where it was. The index is clamped, so
// a block that has changed since still gets the set back, at its end.
export function restoreSetToExercise(
  exercise: WorkoutExerciseDraft,
  set: WorkoutSetDraft,
  index: number,
): WorkoutExerciseDraft {
  const sets = [...exercise.sets]
  sets.splice(Math.min(index, sets.length), 0, set)
  return { ...exercise, sets }
}

// Replace only the targeted set and preserve fields omitted from the partial
// changes object. Mapping avoids mutating the set stored in existing state.
export function updateSetInExercise(
  exercise: WorkoutExerciseDraft,
  setClientId: string,
  changes: WorkoutSetDraftChanges,
): WorkoutExerciseDraft {
  return {
    ...exercise,
    sets: exercise.sets.map((set) =>
      set.clientId === setClientId ? { ...set, ...changes } : set,
    ),
  }
}

// Filtering creates a new set array without the target. This may leave the block
// with zero sets; the UI decides whether the remove action should allow that.
export function removeSetFromExercise(
  exercise: WorkoutExerciseDraft,
  setClientId: string,
): WorkoutExerciseDraft {
  return {
    ...exercise,
    sets: exercise.sets.filter((set) => set.clientId !== setClientId),
  }
}

// What a set holds, as typed, in the same notation as the "last time" line:
// "50 kg × 5", "+10 kg × 8", "12 reps". The Undo line names what was removed
// with it. Empty fields are left out, and an empty set describes as ''.
export function describeDraftSet(
  exercise: Pick<WorkoutExerciseDraft, 'isBodyweight' | 'isAddedWeightEnabled'>,
  set: Pick<WorkoutSetDraft, 'weight' | 'reps'>,
): string {
  const showsWeight = !exercise.isBodyweight || exercise.isAddedWeightEnabled
  const weight = showsWeight ? set.weight.trim() : ''
  const reps = set.reps.trim()
  const load =
    weight === '' ? '' : `${exercise.isBodyweight ? '+' : ''}${weight} kg`

  if (load !== '' && reps !== '') return `${load} × ${reps}`
  if (load !== '') return load
  return reps === '' ? '' : `${reps} reps`
}

export interface PreparedWorkoutDraft {
  workout: CreateWorkoutRequest
  exercises: PutWorkoutExercisesRequest
}

// Where a validation problem is, so the editor can mark that field
// (aria-invalid, described by the message) and move focus to it — opening the
// collapsed page heading first when the fault is one of its fields. Problems
// with no single field to point at ("Add at least one exercise.") say only
// that they're in the exercises.
export type DraftFieldRef =
  | { area: 'heading'; field: 'date' | 'startTime' | 'bodyweightKg' }
  | { area: 'exercises' }
  | {
      area: 'set'
      exerciseIndex: number
      setIndex: number
      field: 'weight' | 'reps'
    }

export type PrepareWorkoutDraftResult =
  | { ok: true; value: PreparedWorkoutDraft }
  | { ok: false; at: DraftFieldRef; message: string }

function optionalText(value: string): string | null {
  const trimmed = value.trim()
  return trimmed === '' ? null : trimmed
}

function parseNonNegativeDecimal(value: string): number | null {
  const trimmed = value.trim()

  // Accept the decimal comma commonly entered on Finnish keyboards, but keep
  // the wire value numeric and culture-independent.
  if (!/^\d+(?:[.,]\d+)?$/.test(trimmed)) {
    return null
  }

  const parsed = Number(trimmed.replace(',', '.'))
  return Number.isFinite(parsed) ? parsed : null
}

function parsePositiveInteger(value: string): number | null {
  const trimmed = value.trim()

  if (!/^[1-9]\d*$/.test(trimmed)) {
    return null
  }

  const parsed = Number(trimmed)
  return Number.isSafeInteger(parsed) ? parsed : null
}

function createLocalTimestamp(date: string, time: string): Date | null {
  const dateMatch = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date)
  const timeMatch = /^(\d{2}):(\d{2})$/.exec(time)

  if (dateMatch === null || timeMatch === null) {
    return null
  }

  const [, yearText, monthText, dayText] = dateMatch
  const [, hourText, minuteText] = timeMatch
  const year = Number(yearText)
  const month = Number(monthText)
  const day = Number(dayText)
  const hour = Number(hourText)
  const minute = Number(minuteText)
  const local = new Date(year, month - 1, day, hour, minute)

  // Date normalizes impossible values (for example 31 February) instead of
  // rejecting them. Comparing every local component prevents that silent shift,
  // including a wall-clock time skipped by a daylight-saving transition.
  if (
    local.getFullYear() !== year ||
    local.getMonth() !== month - 1 ||
    local.getDate() !== day ||
    local.getHours() !== hour ||
    local.getMinutes() !== minute
  ) {
    return null
  }

  return local
}

function createLocalStartedAt(date: string, time: string): string | null {
  return createLocalTimestamp(date, time)?.toISOString() ?? null
}

// A finish time earlier than the start time belongs to the following calendar
// day, which keeps a late-night session editable without adding a second date
// field to the paper-like heading.
export function createLocalEndedAt(
  date: string,
  startTime: string,
  endTime: string,
): string | null {
  const startedAt = createLocalTimestamp(date, startTime)
  const endedAt = createLocalTimestamp(date, endTime)

  if (startedAt === null || endedAt === null) {
    return null
  }

  if (endedAt < startedAt) {
    endedAt.setDate(endedAt.getDate() + 1)
  }

  return endedAt.toISOString()
}

// How long after a page's start "Finish session" may still mean "now". A
// session longer than this is far likelier a page that was never closed out
// (PLAN.md: "you will regularly forget to close a session out") than one that
// really ran this long.
export const FINISH_NOW_WINDOW_HOURS = 6

// Whether finishing must ask when the session ended instead of stamping the
// current time. Stamping "now" on a page started on 30 Sep and finished on
// 7 Oct would record a week-long session; one whose start is still ahead of
// the clock would end before it began. A start that doesn't parse returns
// false: the save rejects it on its own, with its own message.
export function needsExplicitFinishTime(
  heading: Pick<WorkoutHeadingDraft, 'date' | 'startTime'>,
  now: Date,
): boolean {
  const startedAt = createLocalTimestamp(heading.date, heading.startTime)
  if (startedAt === null) return false

  const elapsedMs = now.getTime() - startedAt.getTime()
  return elapsedMs < 0 || elapsedMs > FINISH_NOW_WINDOW_HOURS * 60 * 60 * 1000
}

type PrepareHeadingResult =
  | { ok: true; value: CreateWorkoutRequest }
  | {
      ok: false
      at: Extract<DraftFieldRef, { area: 'heading' }>
      message: string
    }

// The heading half of a save: date and start time as one instant, the
// optional texts trimmed to null, bodyweight as a number.
function prepareHeading(heading: WorkoutHeadingDraft): PrepareHeadingResult {
  const startedAt = createLocalStartedAt(heading.date, heading.startTime)

  if (startedAt === null) {
    // One message covers both fields; point at the date only when the date
    // itself is malformed, otherwise the time is the one that doesn't exist.
    return {
      ok: false,
      at: {
        area: 'heading',
        field: /^\d{4}-\d{2}-\d{2}$/.test(heading.date) ? 'startTime' : 'date',
      },
      message: 'Enter a valid date and start time.',
    }
  }

  let bodyweightKg: number | null = null
  if (heading.bodyweightKg.trim() !== '') {
    bodyweightKg = parseNonNegativeDecimal(heading.bodyweightKg)
    if (bodyweightKg === null || bodyweightKg === 0) {
      return {
        ok: false,
        at: { area: 'heading', field: 'bodyweightKg' },
        message: 'Bodyweight must be a number greater than zero.',
      }
    }
  }

  return {
    ok: true,
    value: {
      date: heading.date,
      startedAt,
      title: optionalText(heading.title),
      bodyweightKg,
      location: optionalText(heading.location),
      notes: optionalText(heading.notes),
    },
  }
}

type PutSetInput =
  PutWorkoutExercisesRequest['exercises'][number]['sets'][number]

// One set row as the API takes it, or the field that stops it. Reps first:
// a row is "complete" exactly when this succeeds, which is what autosave
// sends and what the incomplete-row notice looks for.
function prepareSet(
  exercise: Pick<WorkoutExerciseDraft, 'isBodyweight' | 'isAddedWeightEnabled'>,
  set: WorkoutSetDraft,
): { ok: true; value: PutSetInput } | { ok: false; field: 'weight' | 'reps' } {
  const reps = parsePositiveInteger(set.reps)
  if (reps === null) {
    return { ok: false, field: 'reps' }
  }

  let weight: number | null = null
  if (!exercise.isBodyweight || exercise.isAddedWeightEnabled) {
    weight = parseNonNegativeDecimal(set.weight)
    if (weight === null) {
      return { ok: false, field: 'weight' }
    }
  }

  return { ok: true, value: { weight, reps, isWarmup: set.isWarmup } }
}

// Validates the complete editor state and converts its string fields into the
// API's numeric and timestamp types in one place. A failed conversion never
// produces a partial request for the screen to accidentally submit. This is
// the strict save: Save changes and Finish session, where a half-filled row
// must be fixed or removed rather than silently left out.
export function prepareWorkoutDraft(
  heading: WorkoutHeadingDraft,
  exerciseDrafts: WorkoutExerciseDraft[],
): PrepareWorkoutDraftResult {
  const workout = prepareHeading(heading)
  if (!workout.ok) {
    return workout
  }

  if (exerciseDrafts.length === 0) {
    return {
      ok: false,
      at: { area: 'exercises' },
      message: 'Add at least one exercise.',
    }
  }

  const exercises: PutWorkoutExercisesRequest['exercises'] = []

  for (
    let exerciseIndex = 0;
    exerciseIndex < exerciseDrafts.length;
    exerciseIndex += 1
  ) {
    const exercise = exerciseDrafts[exerciseIndex]
    const exerciseNumber = exerciseIndex + 1

    if (exercise.exerciseName.trim() === '') {
      return {
        ok: false,
        at: { area: 'exercises' },
        message: `Exercise ${exerciseNumber} needs a name.`,
      }
    }

    if (exercise.sets.length === 0) {
      return {
        ok: false,
        at: { area: 'exercises' },
        message: `${exercise.exerciseName} needs at least one set.`,
      }
    }

    const sets: PutSetInput[] = []

    for (let setIndex = 0; setIndex < exercise.sets.length; setIndex += 1) {
      const set = prepareSet(exercise, exercise.sets[setIndex])
      const setNumber = setIndex + 1

      if (!set.ok) {
        return {
          ok: false,
          at: { area: 'set', exerciseIndex, setIndex, field: set.field },
          message:
            set.field === 'reps'
              ? `${exercise.exerciseName}, set ${setNumber}: reps must be a whole number greater than zero.`
              : `${exercise.exerciseName}, set ${setNumber}: enter a valid weight.`,
        }
      }

      sets.push(set.value)
    }

    exercises.push({
      exerciseName: exercise.exerciseName.trim(),
      sets,
    })
  }

  return {
    ok: true,
    value: { workout: workout.value, exercises: { exercises } },
  }
}

// What an autosave sends (specs/003 FR-002): the heading, and only the sets
// that are complete. A row still being typed stays on screen and in the tab's
// draft, and a block with no complete set yet is left out, so the server
// never holds half a set. `blockIndexes[i]` is the draft index of the i-th
// block sent, to match the response's blocks back to the editor's.
export type PrepareAutosaveResult =
  | {
      ok: true
      value: PreparedWorkoutDraft
      blockIndexes: number[]
      setCount: number
    }
  | { ok: false; message: string }

export function prepareAutosaveDraft(
  heading: WorkoutHeadingDraft,
  exerciseDrafts: WorkoutExerciseDraft[],
): PrepareAutosaveResult {
  const workout = prepareHeading(heading)
  if (!workout.ok) {
    return { ok: false, message: workout.message }
  }

  const exercises: PutWorkoutExercisesRequest['exercises'] = []
  const blockIndexes: number[] = []
  let setCount = 0

  exerciseDrafts.forEach((exercise, index) => {
    const name = exercise.exerciseName.trim()
    const sets = exercise.sets.flatMap((set) => {
      const prepared = prepareSet(exercise, set)
      return prepared.ok ? [prepared.value] : []
    })
    if (name === '' || sets.length === 0) {
      return
    }
    exercises.push({ exerciseName: name, sets })
    blockIndexes.push(index)
    setCount += sets.length
  })

  return {
    ok: true,
    value: { workout: workout.value, exercises: { exercises } },
    blockIndexes,
    setCount,
  }
}

// A row that has something written in it but can't be saved yet: "a weight
// but no reps". An untouched row isn't one: there's nothing to lose, and the
// notice would only nag about the row "+ Add set" just opened.
export interface IncompleteSet {
  setClientId: string
  exerciseName: string
  setNumber: number
  needs: 'weight' | 'reps'
}

export function findIncompleteSet(
  exercises: WorkoutExerciseDraft[],
): IncompleteSet | null {
  for (const exercise of exercises) {
    const showsWeight = !exercise.isBodyweight || exercise.isAddedWeightEnabled
    for (let index = 0; index < exercise.sets.length; index += 1) {
      const set = exercise.sets[index]
      const isBlank =
        set.reps.trim() === '' && (!showsWeight || set.weight.trim() === '')
      if (isBlank) continue
      const prepared = prepareSet(exercise, set)
      if (!prepared.ok) {
        return {
          setClientId: set.clientId,
          exerciseName: exercise.exerciseName,
          setNumber: index + 1,
          needs: prepared.field,
        }
      }
    }
  }
  return null
}

// "Bench Press set 3 needs reps". The exercise is named because "set 3" alone
// could be in any block.
export function describeIncompleteSet(set: IncompleteSet): string {
  return `${set.exerciseName} set ${set.setNumber} needs ${set.needs}`
}

// After a conflict (specs/003 D10) the editor reloads the server's page, and
// whatever existed only in this tab is listed rather than merged: "Bench Press
// set 4 · 80 kg × 5". A set counts as on the server when the server's page
// has an equal one (same exercise, figures and warm-up flag) not already
// matched to another, so two identical sets here need two there.
export function describeUnsavedSets(
  local: WorkoutExerciseDraft[],
  server: WorkoutExerciseDraft[],
): string[] {
  const setKey = (
    exercise: WorkoutExerciseDraft,
    set: WorkoutSetDraft,
  ): string | null => {
    const prepared = prepareSet(exercise, set)
    if (!prepared.ok) return null
    const { weight, reps, isWarmup } = prepared.value
    return JSON.stringify([
      normalizeExerciseName(exercise.exerciseName),
      weight,
      reps,
      isWarmup,
    ])
  }

  const available = new Map<string, number>()
  for (const exercise of server) {
    for (const set of exercise.sets) {
      const key = setKey(exercise, set)
      if (key !== null) available.set(key, (available.get(key) ?? 0) + 1)
    }
  }

  const unsaved: string[] = []
  for (const exercise of local) {
    exercise.sets.forEach((set, index) => {
      const figures = describeDraftSet(exercise, set)
      if (figures === '') return
      const key = setKey(exercise, set)
      const count = key === null ? 0 : (available.get(key) ?? 0)
      if (key !== null && count > 0) {
        available.set(key, count - 1)
        return
      }
      unsaved.push(`${exercise.exerciseName} set ${index + 1} · ${figures}`)
    })
  }
  return unsaved
}
