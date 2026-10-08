import { describe, expect, it } from 'vitest'
import { precheckBackupFile, type BackupFile } from './backupFile'

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
      chosen(JSON.stringify({ ...backup, formatVersion: 2 })),
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
})
