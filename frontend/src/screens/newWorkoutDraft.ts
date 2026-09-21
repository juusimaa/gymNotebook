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
