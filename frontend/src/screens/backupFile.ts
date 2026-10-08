// The browser checks a selected file before offering to upload it. The server
// remains the authority: it validates the original bytes again in one transaction.
export const DEFAULT_RESTORE_MAX_BYTES = 26_214_400

export type BackupFile = {
  formatVersion: 1
  snapshotAt: string
  exercises: { id: number; name: string; isBodyweight: boolean }[]
  workouts: {
    id: number
    date: string
    startedAt: string
    endedAt: string | null
    title: string | null
    location: string | null
    notes: string | null
    bodyweightKg: number | null
  }[]
  workoutExercises: {
    id: number
    workoutId: number
    exerciseId: number
    position: number
  }[]
  sets: {
    id: number
    workoutExerciseId: number
    setNumber: number
    weight: number | null
    reps: number
    isWarmup: boolean
  }[]
}

export type BackupSummary = {
  takenAt: string
  pages: number
  firstDate: string | null
  lastDate: string | null
  sets: number
  exercises: number
}

export type BackupFileResult =
  | { ok: true; file: BackupFile; summary: BackupSummary }
  | {
      ok: false
      reason:
        | 'too_large'
        | 'not_a_backup'
        | 'unsupported_version'
        | 'damaged'
        | 'broken_reference'
        | 'invalid_value'
    }

type FailureReason = Extract<BackupFileResult, { ok: false }>['reason']

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function id(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) > 0
}

function instant(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    /^\d{4}-\d\d-\d\dT/.test(value) &&
    !Number.isNaN(Date.parse(value))
  )
}

function date(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d\d-\d\d$/.test(value))
    return false
  const parsed = new Date(`${value}T00:00:00Z`)
  return (
    !Number.isNaN(parsed.valueOf()) &&
    parsed.toISOString().slice(0, 10) === value
  )
}

function optionalText(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function decimal(value: unknown, maximum: number): value is number | null {
  return (
    value === null ||
    (typeof value === 'number' &&
      Number.isFinite(value) &&
      value >= 0 &&
      value <= maximum &&
      (String(value).split('.')[1]?.length ?? 0) <= 2)
  )
}

// Mirrors the restore endpoint's shape, references and recorded-value checks.
// Extra account/privacy fields are intentionally ignored; they are not restored.
function validate(value: unknown): FailureReason | null {
  if (
    !record(value) ||
    !('formatVersion' in value) ||
    !Array.isArray(value.exercises) ||
    !Array.isArray(value.workouts) ||
    !Array.isArray(value.workoutExercises) ||
    !Array.isArray(value.sets)
  )
    return 'not_a_backup'
  if (value.formatVersion !== 1) return 'unsupported_version'
  if (!instant(value.snapshotAt)) return 'invalid_value'

  const exerciseIds = new Set<number>()
  const workoutIds = new Set<number>()
  const blockIds = new Set<number>()
  const setIds = new Set<number>()
  const starts = new Set<number>()
  const positions = new Set<string>()
  const setNumbers = new Set<string>()
  for (const exercise of value.exercises) {
    if (
      !record(exercise) ||
      !id(exercise.id) ||
      exerciseIds.has(exercise.id) ||
      typeof exercise.name !== 'string' ||
      !exercise.name.trim() ||
      typeof exercise.isBodyweight !== 'boolean'
    )
      return 'invalid_value'
    exerciseIds.add(exercise.id)
  }
  for (const workout of value.workouts) {
    if (
      !record(workout) ||
      !id(workout.id) ||
      workoutIds.has(workout.id) ||
      !date(workout.date) ||
      !instant(workout.startedAt) ||
      !(workout.endedAt === null || instant(workout.endedAt)) ||
      (workout.endedAt !== null &&
        Date.parse(workout.endedAt) < Date.parse(workout.startedAt)) ||
      !optionalText(workout.title) ||
      !optionalText(workout.location) ||
      !optionalText(workout.notes) ||
      !decimal(workout.bodyweightKg, 999.99)
    )
      return 'invalid_value'
    const start = Date.parse(workout.startedAt)
    if (starts.has(start)) return 'invalid_value'
    starts.add(start)
    workoutIds.add(workout.id)
  }
  for (const block of value.workoutExercises) {
    if (
      !record(block) ||
      !id(block.id) ||
      blockIds.has(block.id) ||
      !id(block.workoutId) ||
      !id(block.exerciseId) ||
      !Number.isSafeInteger(block.position) ||
      (block.position as number) < 0 ||
      positions.has(`${String(block.workoutId)}:${String(block.position)}`)
    )
      return 'invalid_value'
    if (!workoutIds.has(block.workoutId) || !exerciseIds.has(block.exerciseId))
      return 'broken_reference'
    blockIds.add(block.id)
    positions.add(`${String(block.workoutId)}:${String(block.position)}`)
  }
  for (const set of value.sets) {
    if (
      !record(set) ||
      !id(set.id) ||
      setIds.has(set.id) ||
      !id(set.workoutExerciseId) ||
      !id(set.setNumber) ||
      !id(set.reps) ||
      typeof set.isWarmup !== 'boolean' ||
      !decimal(set.weight, 9999.99) ||
      setNumbers.has(
        `${String(set.workoutExerciseId)}:${String(set.setNumber)}`,
      )
    )
      return 'invalid_value'
    if (!blockIds.has(set.workoutExerciseId)) return 'broken_reference'
    setIds.add(set.id)
    setNumbers.add(`${set.workoutExerciseId}:${set.setNumber}`)
  }
  return null
}

export async function precheckBackupFile(
  file: File,
  maxBytes = DEFAULT_RESTORE_MAX_BYTES,
): Promise<BackupFileResult> {
  if (file.size > maxBytes) return { ok: false, reason: 'too_large' }
  if (!file.name.toLowerCase().endsWith('.json'))
    return { ok: false, reason: 'not_a_backup' }
  let parsed: unknown
  try {
    parsed = JSON.parse(await file.text()) as unknown
  } catch {
    return { ok: false, reason: 'damaged' }
  }
  const reason = validate(parsed)
  if (reason !== null) return { ok: false, reason }

  const backup = parsed as BackupFile
  const dates = backup.workouts.map((workout) => workout.date).sort()
  return {
    ok: true,
    file: backup,
    summary: {
      takenAt: backup.snapshotAt,
      pages: backup.workouts.length,
      firstDate: dates[0] ?? null,
      lastDate: dates.at(-1) ?? null,
      sets: backup.sets.length,
      exercises: backup.exercises.length,
    },
  }
}
