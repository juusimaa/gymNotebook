import type { ExerciseResponse } from '../api/exercises'

// The same compact set notation appears in autocomplete results, the exercise
// index and the editor's "last time" line. Bodyweight exercises prefix a
// non-null weight because it is added load.
export function formatLastSet(
  lastSet: NonNullable<ExerciseResponse['lastSet']>,
  isBodyweight: boolean,
): string {
  if (lastSet.weight === null) {
    return `${lastSet.reps} reps`
  }

  const prefix = isBodyweight ? '+' : ''
  return `${prefix}${lastSet.weight} kg × ${lastSet.reps}`
}

export function describeLastSet(exercise: ExerciseResponse): string {
  return exercise.lastSet === null
    ? 'Not logged yet'
    : formatLastSet(exercise.lastSet, exercise.isBodyweight)
}

export function normalizeExerciseName(name: string): string {
  return name.trim().toLowerCase().replace(/\s+/g, ' ')
}
