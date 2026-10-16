import type { ExerciseResponse } from '../api/exercises'

// The same compact set notation appears in autocomplete results, the exercise
// index and the editor's "last time" line. Bodyweight exercises prefix a
// non-null weight because it is added load. A warm-up says so: the server only
// returns one when the latest session had nothing else, and "40 kg × 8" alone
// would read as the weight to work at (honest numbers, PRODUCT.md).
export function formatLastSet(
  lastSet: NonNullable<ExerciseResponse['lastSet']>,
  isBodyweight: boolean,
): string {
  const suffix = lastSet.isWarmup ? ' · warm-up' : ''
  if (lastSet.weight === null) {
    return `${lastSet.reps} reps${suffix}`
  }

  const prefix = isBodyweight ? '+' : ''
  return `${prefix}${lastSet.weight} kg × ${lastSet.reps}${suffix}`
}

export function describeLastSet(exercise: ExerciseResponse): string {
  return exercise.lastSet === null
    ? 'Not logged yet'
    : formatLastSet(exercise.lastSet, exercise.isBodyweight)
}

export function normalizeExerciseName(name: string): string {
  return name.trim().toLowerCase().replace(/\s+/g, ' ')
}

// Progress filters the exercise list it already holds, matching a name the way
// GET /exercises?search= does on the server (NormalizedName.Contains), so
// "taka" finds "Takakyykky" here just as it does in the Exercises index.
export function filterExercisesByName<T extends { name: string }>(
  exercises: T[],
  query: string,
): T[] {
  const wanted = normalizeExerciseName(query)
  if (wanted === '') {
    return exercises
  }

  return exercises.filter((exercise) =>
    normalizeExerciseName(exercise.name).includes(wanted),
  )
}
