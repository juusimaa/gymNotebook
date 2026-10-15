// The browser checks a selected file before offering to upload it. The server
// remains the authority: it validates the original bytes again in one transaction.
export const DEFAULT_RESTORE_MAX_BYTES = 26_214_400

export type SetStatus = 'planned' | 'done' | 'skipped'

// Version 2 (specs/006 research R10) adds prepared pages (date and startedAt
// null), a status on every set and the three template arrays. A version 1
// file has none of them; its sets are all lifted ones.
export type BackupFile = {
  formatVersion: 1 | 2
  snapshotAt: string
  exercises: { id: number; name: string; isBodyweight: boolean }[]
  workouts: {
    id: number
    date: string | null
    startedAt: string | null
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
    status?: SetStatus
  }[]
  templates?: { id: number; name: string }[]
  templateExercises?: {
    id: number
    templateId: number
    exerciseId: number
    position: number
  }[]
  templateSets?: {
    id: number
    templateExerciseId: number
    setNumber: number
    weight: number | null
    reps: number
    isWarmup: boolean
  }[]
}

export type BackupSummary = {
  takenAt: string
  // Started pages only, with their date range; prepared pages and templates
  // are counted by planCounts.
  pages: number
  firstDate: string | null
  lastDate: string | null
  sets: number
  exercises: number
}

// What a version 2 file holds besides its history (specs/006): prepared pages,
// which restore always adds, and templates, which it adds by name.
export function planCounts(file: BackupFile): {
  plans: number
  templates: number
} {
  return {
    plans: file.workouts.filter((workout) => workout.startedAt === null).length,
    templates: file.templates?.length ?? 0,
  }
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

// Which set statuses a page's state allows, as the server's SetStatusRules
// (specs/006 research R3): prepared → planned; in progress → planned or done;
// finished → done or skipped.
function statusFits(status: unknown, started: boolean, ended: boolean) {
  if (!started) return status === 'planned'
  if (!ended) return status === 'planned' || status === 'done'
  return status === 'done' || status === 'skipped'
}

// The name rule the server uses for exercise and template names: trimmed,
// lowercased, inner whitespace collapsed (ExerciseNameNormalizer).
function normalizeName(name: string) {
  return name.trim().toLowerCase().replace(/\s+/g, ' ')
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
  if (value.formatVersion !== 1 && value.formatVersion !== 2)
    return 'unsupported_version'
  const isV2 = value.formatVersion === 2
  if (
    isV2 &&
    (!Array.isArray(value.templates) ||
      !Array.isArray(value.templateExercises) ||
      !Array.isArray(value.templateSets))
  )
    return 'not_a_backup'
  if (!instant(value.snapshotAt)) return 'invalid_value'

  const exerciseIds = new Set<number>()
  const workoutIds = new Set<number>()
  const blockIds = new Set<number>()
  const setIds = new Set<number>()
  const starts = new Set<number>()
  const positions = new Set<string>()
  const setNumbers = new Set<string>()
  // Each page's state, for the set status check (version 2).
  const pageStates = new Map<number, { started: boolean; ended: boolean }>()
  const blockPages = new Map<number, number>()
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
      !optionalText(workout.title) ||
      !optionalText(workout.location) ||
      !optionalText(workout.notes) ||
      !decimal(workout.bodyweightKg, 999.99)
    )
      return 'invalid_value'
    // A prepared page (version 2) has no date, start or end; any other page
    // has a date and a unique start, as in version 1.
    const prepared =
      isV2 &&
      workout.date === null &&
      workout.startedAt === null &&
      workout.endedAt === null
    if (!prepared) {
      if (
        !date(workout.date) ||
        !instant(workout.startedAt) ||
        !(workout.endedAt === null || instant(workout.endedAt)) ||
        (workout.endedAt !== null &&
          Date.parse(workout.endedAt) < Date.parse(workout.startedAt))
      )
        return 'invalid_value'
      const start = Date.parse(workout.startedAt)
      if (starts.has(start)) return 'invalid_value'
      starts.add(start)
    }
    workoutIds.add(workout.id)
    pageStates.set(workout.id, {
      started: !prepared,
      ended: !prepared && workout.endedAt !== null,
    })
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
    blockPages.set(block.id, block.workoutId)
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
    if (isV2) {
      const page = pageStates.get(blockPages.get(set.workoutExerciseId) ?? 0)
      if (!page || !statusFits(set.status, page.started, page.ended))
        return 'invalid_value'
    }
    setIds.add(set.id)
    setNumbers.add(`${set.workoutExerciseId}:${set.setNumber}`)
  }
  if (!isV2) return null

  // Templates: the page tables' id, position, set-number and value rules,
  // plus names unique within the file once normalized.
  const templateIds = new Set<number>()
  const templateNames = new Set<string>()
  const templateBlockIds = new Set<number>()
  const templatePositions = new Set<string>()
  const templateSetIds = new Set<number>()
  const templateSetNumbers = new Set<string>()
  for (const template of value.templates as unknown[]) {
    if (
      !record(template) ||
      !id(template.id) ||
      templateIds.has(template.id) ||
      typeof template.name !== 'string' ||
      !template.name.trim() ||
      templateNames.has(normalizeName(template.name))
    )
      return 'invalid_value'
    templateIds.add(template.id)
    templateNames.add(normalizeName(template.name))
  }
  for (const block of value.templateExercises as unknown[]) {
    if (
      !record(block) ||
      !id(block.id) ||
      templateBlockIds.has(block.id) ||
      !id(block.templateId) ||
      !id(block.exerciseId) ||
      !Number.isSafeInteger(block.position) ||
      (block.position as number) < 0 ||
      templatePositions.has(
        `${String(block.templateId)}:${String(block.position)}`,
      )
    )
      return 'invalid_value'
    if (
      !templateIds.has(block.templateId) ||
      !exerciseIds.has(block.exerciseId)
    )
      return 'broken_reference'
    templateBlockIds.add(block.id)
    templatePositions.add(
      `${String(block.templateId)}:${String(block.position)}`,
    )
  }
  for (const set of value.templateSets as unknown[]) {
    if (
      !record(set) ||
      !id(set.id) ||
      templateSetIds.has(set.id) ||
      !id(set.templateExerciseId) ||
      !id(set.setNumber) ||
      !id(set.reps) ||
      typeof set.isWarmup !== 'boolean' ||
      !decimal(set.weight, 9999.99) ||
      templateSetNumbers.has(
        `${String(set.templateExerciseId)}:${String(set.setNumber)}`,
      )
    )
      return 'invalid_value'
    if (!templateBlockIds.has(set.templateExerciseId)) return 'broken_reference'
    templateSetIds.add(set.id)
    templateSetNumbers.add(
      `${String(set.templateExerciseId)}:${String(set.setNumber)}`,
    )
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
  const dates = backup.workouts
    .flatMap((workout) => (workout.date === null ? [] : [workout.date]))
    .sort()
  return {
    ok: true,
    file: backup,
    summary: {
      takenAt: backup.snapshotAt,
      pages: dates.length,
      firstDate: dates[0] ?? null,
      lastDate: dates.at(-1) ?? null,
      sets: backup.sets.length,
      exercises: backup.exercises.length,
    },
  }
}
