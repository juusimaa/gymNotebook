import type {
  CreateWorkoutRequest,
  PutWorkoutExercisesRequest,
  WorkoutDetailResponse,
} from '../api/workouts'

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
  sets: WorkoutSetDraft[]
}

// A new exercise block always begins with one editable set. Both client IDs are
// supplied by the caller so this factory stays deterministic and easy to test.
export function createWorkoutExerciseDraft(
  clientId: string,
  initialSetClientId: string,
  exerciseId: number | null,
  exerciseName: string,
  isBodyweight: boolean,
): WorkoutExerciseDraft {
  return {
    clientId,
    exerciseId,
    exerciseName,
    isBodyweight,
    isAddedWeightEnabled: false,
    sets: [createEmptySetDraft(initialSetClientId)],
  }
}

// Return a new block and set array so React can observe the state change; the
// original draft remains untouched.
export function addEmptySetToExercise(
  exercise: WorkoutExerciseDraft,
  setClientId: string,
): WorkoutExerciseDraft {
  return {
    ...exercise,
    sets: [...exercise.sets, createEmptySetDraft(setClientId)],
  }
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

export interface PreparedWorkoutDraft {
  workout: CreateWorkoutRequest
  exercises: PutWorkoutExercisesRequest
}

export type PrepareWorkoutDraftResult =
  { ok: true; value: PreparedWorkoutDraft } | { ok: false; message: string }

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
    return { ok: false, message: 'Enter a valid date and start time.' }
  }

  let bodyweightKg: number | null = null
  if (heading.bodyweightKg.trim() !== '') {
    bodyweightKg = parseNonNegativeDecimal(heading.bodyweightKg)
    if (bodyweightKg === null || bodyweightKg === 0) {
      return {
        ok: false,
        message: 'Bodyweight must be a number greater than zero.',
      }
    }
  }

  if (exerciseDrafts.length === 0) {
    return { ok: false, message: 'Add at least one exercise.' }
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
        message: `Exercise ${exerciseNumber} needs a name.`,
      }
    }

    if (exercise.sets.length === 0) {
      return {
        ok: false,
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
