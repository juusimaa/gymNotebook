import { describe, expect, it } from 'vitest'
import { planCounts, precheckBackupFile, type BackupFile } from './backupFile'

const backup: BackupFile = {
  formatVersion: 1,
  snapshotAt: '2026-10-08T12:00:00Z',
  exercises: [{ id: 4, name: 'Sääriprässi', isBodyweight: false }],
  workouts: [
    {
      id: 9,
      date: '2026-10-03',
      startedAt: '2026-10-03T06:34:00Z',
      endedAt: null,
      title: null,
      location: null,
      notes: null,
      bodyweightKg: null,
    },
  ],
  workoutExercises: [{ id: 12, workoutId: 9, exerciseId: 4, position: 0 }],
  sets: [
    {
      id: 20,
      workoutExerciseId: 12,
      setNumber: 1,
      weight: 142.5,
      reps: 8,
      isWarmup: false,
    },
  ],
}

function chosen(contents: string, name = 'gym-notebook.json'): File {
  return new File([contents], name, { type: 'application/json' })
}

describe('precheckBackupFile', () => {
  it('summarises a valid backup before upload', async () => {
    expect(await precheckBackupFile(chosen(JSON.stringify(backup)))).toEqual({
      ok: true,
      file: backup,
      summary: {
        takenAt: backup.snapshotAt,
        pages: 1,
        firstDate: '2026-10-03',
        lastDate: '2026-10-03',
        sets: 1,
        exercises: 1,
      },
    })
  })

  it('summarises an empty notebook without invented dates', async () => {
    const empty = { ...backup, workouts: [], workoutExercises: [], sets: [] }
    const result = await precheckBackupFile(chosen(JSON.stringify(empty)))
    expect(result.ok && result.summary).toMatchObject({
      pages: 0,
      firstDate: null,
      lastDate: null,
      sets: 0,
    })
  })

  it('checks the configured byte limit before reading', async () => {
    const file = chosen(JSON.stringify(backup))
    expect(await precheckBackupFile(file, file.size - 1)).toEqual({
      ok: false,
      reason: 'too_large',
    })
  })

  it.each([
    ['CSV', chosen('Date,Start\r\n', 'backup.csv'), 'not_a_backup'],
    ['other JSON', chosen('{}'), 'not_a_backup'],
    [
      'newer format',
      chosen(JSON.stringify({ ...backup, formatVersion: 3 })),
      'unsupported_version',
    ],
    ['cut-off JSON', chosen('{"formatVersion":1,'), 'damaged'],
    [
      'broken reference',
      chosen(
        JSON.stringify({
          ...backup,
          workoutExercises: [
            { ...backup.workoutExercises[0], exerciseId: 999 },
          ],
        }),
      ),
      'broken_reference',
    ],
    [
      'invalid value',
      chosen(
        JSON.stringify({ ...backup, sets: [{ ...backup.sets[0], reps: -1 }] }),
      ),
      'invalid_value',
    ],
  ])('rejects %s locally', async (_, file, reason) => {
    expect(await precheckBackupFile(file)).toEqual({ ok: false, reason })
  })

  // specs/006 version 2: a finished page with a done and a skipped set, a
  // prepared page with a planned set, and one template.
  const v2: BackupFile = {
    ...backup,
    formatVersion: 2,
    workouts: [
      { ...backup.workouts[0], endedAt: '2026-10-03T07:30:00Z' },
      {
        ...backup.workouts[0],
        id: 10,
        date: null,
        startedAt: null,
        endedAt: null,
      },
    ],
    workoutExercises: [
      ...backup.workoutExercises,
      { id: 13, workoutId: 10, exerciseId: 4, position: 0 },
    ],
    sets: [
      { ...backup.sets[0], status: 'done' },
      { ...backup.sets[0], id: 21, setNumber: 2, status: 'skipped' },
      { ...backup.sets[0], id: 22, workoutExerciseId: 13, status: 'planned' },
    ],
    templates: [{ id: 30, name: 'Push A' }],
    templateExercises: [{ id: 31, templateId: 30, exerciseId: 4, position: 0 }],
    templateSets: [
      {
        id: 32,
        templateExerciseId: 31,
        setNumber: 1,
        weight: 60,
        reps: 8,
        isWarmup: true,
      },
    ],
  }

  it('accepts version 2 and summarises plans apart from pages', async () => {
    const result = await precheckBackupFile(chosen(JSON.stringify(v2)))
    expect(result.ok && result.summary).toMatchObject({
      pages: 1,
      firstDate: '2026-10-03',
      lastDate: '2026-10-03',
      sets: 3,
    })
    expect(planCounts(v2)).toEqual({ plans: 1, templates: 1 })
    expect(planCounts(backup)).toEqual({ plans: 0, templates: 0 })
  })

  it.each<[string, BackupFile, string]>([
    [
      'version 2 without templates',
      { ...v2, templates: undefined },
      'not_a_backup',
    ],
    [
      'a prepared page with a start',
      {
        ...v2,
        workouts: [
          v2.workouts[0],
          { ...v2.workouts[1], startedAt: '2026-10-04T06:00:00Z' },
        ],
      },
      'invalid_value',
    ],
    [
      'a done set on a prepared page',
      {
        ...v2,
        sets: [v2.sets[0], v2.sets[1], { ...v2.sets[2], status: 'done' }],
      },
      'invalid_value',
    ],
    [
      'a planned set on a finished page',
      { ...v2, sets: [{ ...v2.sets[0], status: 'planned' }, v2.sets[2]] },
      'invalid_value',
    ],
    [
      'duplicate template names',
      {
        ...v2,
        templates: [
          { id: 30, name: 'Push A' },
          { id: 33, name: ' push  a' },
        ],
      },
      'invalid_value',
    ],
    [
      'a template block naming a missing exercise',
      {
        ...v2,
        templateExercises: [
          { id: 31, templateId: 30, exerciseId: 999, position: 0 },
        ],
      },
      'broken_reference',
    ],
  ])('rejects %s locally', async (_, file, reason) => {
    expect(await precheckBackupFile(chosen(JSON.stringify(file)))).toEqual({
      ok: false,
      reason,
    })
  })
})
