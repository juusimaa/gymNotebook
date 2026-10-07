import type { ExerciseResponse } from '../api/exercises'
import type {
  CreateWorkoutRequest,
  PutWorkoutExercisesRequest,
  WorkoutDetailResponse,
} from '../api/workouts'
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
  // the block. Only a block picked from autocomplete has one, on a new page or
  // on the edit page of a session still in progress — which asks the server to
  // leave the page itself out, so the hint is never one of its own sets.
  // Optional so drafts stored before it existed still restore. Never sent.
  lastSet?: ExerciseResponse['lastSet']
  sets: WorkoutSetDraft[]
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
// starts the same way — "+ Add set" then repeats it and the working weight is
// typed over the copy; otherwise its final working set (lastSet). Both client
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
// set repeats the one above it — weight, reps and the warm-up flag — so a
// repeated set costs one tap instead of retyping both numbers. The first set
// of a block has nothing above it and starts empty. Returns a new block and
// set array so React can observe the change; the original stays untouched.
export function addSetToExercise(
  exercise: WorkoutExerciseDraft,
  setClientId: string,
): WorkoutExerciseDraft {
  const previous = exercise.sets.at(-1)
  const set =
    previous === undefined
      ? createEmptySetDraft(setClientId)
      : { ...previous, clientId: setClientId }
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

// Validates the complete editor state and converts its string fields into the
// API's numeric and timestamp types in one place. A failed conversion never
// produces a partial request for the screen to accidentally submit.
export function prepareWorkoutDraft(
  heading: WorkoutHeadingDraft,
  exerciseDrafts: WorkoutExerciseDraft[],
): PrepareWorkoutDraftResult {
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

    const sets: PutWorkoutExercisesRequest['exercises'][number]['sets'] = []

    for (let setIndex = 0; setIndex < exercise.sets.length; setIndex += 1) {
      const set = exercise.sets[setIndex]
      const setNumber = setIndex + 1
      const reps = parsePositiveInteger(set.reps)

      if (reps === null) {
        return {
          ok: false,
          at: { area: 'set', exerciseIndex, setIndex, field: 'reps' },
          message: `${exercise.exerciseName}, set ${setNumber}: reps must be a whole number greater than zero.`,
        }
      }

      let weight: number | null = null
      const needsWeight =
        !exercise.isBodyweight || exercise.isAddedWeightEnabled

      if (needsWeight) {
        weight = parseNonNegativeDecimal(set.weight)
        if (weight === null) {
          return {
            ok: false,
            at: { area: 'set', exerciseIndex, setIndex, field: 'weight' },
            message: `${exercise.exerciseName}, set ${setNumber}: enter a valid weight.`,
          }
        }
      }

      sets.push({ weight, reps, isWarmup: set.isWarmup })
    }

    exercises.push({
      exerciseName: exercise.exerciseName.trim(),
      sets,
    })
  }

  return {
    ok: true,
    value: {
      workout: {
        date: heading.date,
        startedAt,
        title: optionalText(heading.title),
        bodyweightKg,
        location: optionalText(heading.location),
        notes: optionalText(heading.notes),
      },
      exercises: { exercises },
    },
  }
}
