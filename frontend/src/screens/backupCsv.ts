import type { BackupFile } from './backupFile'

const HEADERS = [
  'Date',
  'Start',
  'End',
  'Title',
  'Gym',
  'Bodyweight (kg)',
  'Notes',
  'Block',
  'Exercise',
  'Bodyweight exercise',
  'Set',
  'Warm-up',
  'Weight (kg)',
  'Reps',
]

export type CsvOptions = { timeZone: string; decimalComma: boolean }

export function prefersDecimalComma(locale: string): boolean {
  return (
    new Intl.NumberFormat(locale)
      .formatToParts(1.5)
      .find((part) => part.type === 'decimal')?.value === ','
  )
}

export function toCsv(file: BackupFile, options: CsvOptions): string {
  const separator = options.decimalComma ? ';' : ','
  const time = new Intl.DateTimeFormat('en-GB', {
    timeZone: options.timeZone,
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  })
  const clock = (instant: string | null) =>
    instant === null ? '' : time.format(new Date(instant))
  const number = (value: number | null) =>
    value === null
      ? ''
      : String(value).replace('.', options.decimalComma ? ',' : '.')
  const cell = (value: string, isText = false) => {
    // A leading quote forces spreadsheet software to treat formula-shaped text
    // as text. Quote the complete field after applying that guard.
    const guarded = isText && /^[=+\-@\t\r]/.test(value) ? `'${value}` : value
    return guarded.includes(separator) || /["\r\n]/.test(guarded)
      ? `"${guarded.replaceAll('"', '""')}"`
      : guarded
  }
  const rows: string[] = [HEADERS.map((header) => cell(header)).join(separator)]
  const exercises = new Map(
    file.exercises.map((exercise) => [exercise.id, exercise]),
  )
  const blocks = new Map<number, BackupFile['workoutExercises']>()
  const sets = new Map<number, BackupFile['sets']>()
  for (const block of file.workoutExercises) {
    const entries = blocks.get(block.workoutId) ?? []
    entries.push(block)
    blocks.set(block.workoutId, entries)
  }
  for (const set of file.sets) {
    const entries = sets.get(set.workoutExerciseId) ?? []
    entries.push(set)
    sets.set(set.workoutExerciseId, entries)
  }

  const workouts = [...file.workouts].sort(
    (a, b) =>
      a.date.localeCompare(b.date) || a.startedAt.localeCompare(b.startedAt),
  )
  for (const workout of workouts) {
    const page = [
      cell(workout.date),
      cell(clock(workout.startedAt)),
      cell(clock(workout.endedAt)),
      cell(workout.title ?? '', true),
      cell(workout.location ?? '', true),
      cell(number(workout.bodyweightKg)),
      cell(workout.notes ?? '', true),
    ]
    const pageBlocks = (blocks.get(workout.id) ?? []).sort(
      (a, b) => a.position - b.position || a.id - b.id,
    )
    if (pageBlocks.length === 0) {
      rows.push([...page, ...Array<string>(7).fill('')].join(separator))
      continue
    }
    for (const block of pageBlocks) {
      const exercise = exercises.get(block.exerciseId)
      const prefix = [
        ...page,
        cell(String(block.position + 1)),
        cell(exercise?.name ?? '', true),
        cell(exercise?.isBodyweight ? 'yes' : 'no'),
      ]
      const blockSets = (sets.get(block.id) ?? []).sort(
        (a, b) => a.setNumber - b.setNumber || a.id - b.id,
      )
      if (blockSets.length === 0) {
        rows.push([...prefix, '', '', '', ''].join(separator))
        continue
      }
      for (const set of blockSets) {
        rows.push(
          [
            ...prefix,
            cell(String(set.setNumber)),
            cell(set.isWarmup ? 'yes' : 'no'),
            cell(number(set.weight)),
            cell(String(set.reps)),
          ].join(separator),
        )
      }
    }
  }
  return `\uFEFF${rows.join('\r\n')}\r\n`
}
