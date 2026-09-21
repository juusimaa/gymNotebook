import type { ExerciseResponse } from '../api/exercises'

// The same compact set notation appears in autocomplete results and the exercise
// index. Bodyweight exercises prefix a non-null weight because it is added load.
export function describeLastSet(exercise: ExerciseResponse): string {
  const lastSet = exercise.lastSet

  if (lastSet === null) {
    return 'Not logged yet'
  }

  if (lastSet.weight === null) {
    return `${lastSet.reps} reps`
  }

  const prefix = exercise.isBodyweight ? '+' : ''
  return `${prefix}${lastSet.weight} kg × ${lastSet.reps}`
}

export function normalizeExerciseName(name: string): string {
  return name.trim().toLowerCase().replace(/\s+/g, ' ')
}
