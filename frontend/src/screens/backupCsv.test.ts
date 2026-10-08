import { describe, expect, it } from 'vitest'
import { prefersDecimalComma, toCsv } from './backupCsv'
import type { BackupFile } from './backupFile'

const file: BackupFile = {
  formatVersion: 1,
  snapshotAt: '2026-10-08T12:00:00Z',
  exercises: [{ id: 1, name: 'Sääriprässi', isBodyweight: false }],
  workouts: [
    {
      id: 1,
      date: '2026-10-03',
      startedAt: '2026-10-02T21:30:00Z',
      endedAt: '2026-10-02T22:05:00Z',
      title: 'Push A',
      location: 'Kamppi',
      notes: 'Hartia jumissa; "kevyt" päivä\n=note',
      bodyweightKg: 82.4,
    },
  ],
  workoutExercises: [{ id: 2, workoutId: 1, exerciseId: 1, position: 1 }],
  sets: [
    {
      id: 3,
      workoutExerciseId: 2,
      setNumber: 3,
      weight: 142.5,
      reps: 8,
      isWarmup: false,
    },
  ],
}

describe('backup CSV', () => {
  it.each([
    ['fi-FI', true],
    ['sv-SE', true],
    ['de-DE', true],
    ['en-US', false],
  ])('detects the decimal separator of %s', (locale, expected) => {
    expect(prefersDecimalComma(locale)).toBe(expected)
  })

  it('writes BOM, CRLF, local times across midnight and Finnish decimals', () => {
    const csv = toCsv(file, { timeZone: 'Europe/Helsinki', decimalComma: true })
    expect(csv.startsWith('\uFEFFDate;Start;End;')).toBe(true)
    expect(csv.endsWith('\r\n')).toBe(true)
    expect(csv).toContain('2026-10-03;00:30;01:05;Push A;Kamppi;82,4;')
    expect(csv).toContain(
      '"Hartia jumissa; ""kevyt"" päivä\n=note";2;Sääriprässi;no;3;no;142,5;8',
    )
  })

  it('uses commas and decimal points for US spreadsheets', () => {
    const csv = toCsv(file, {
      timeZone: 'America/Los_Angeles',
      decimalComma: false,
    })
    expect(csv).toContain('2026-10-03,14:30,15:05,Push A,Kamppi,82.4,')
    expect(csv).toContain(',142.5,8\r\n')
  })

  it.each(['=SUM(1,1)', '+cmd', '-cmd', '@cmd', '\tcmd', '\rcmd'])(
    'guards formula-shaped text %s',
    (name) => {
      const csv = toCsv(
        { ...file, exercises: [{ ...file.exercises[0], name }] },
        { timeZone: 'UTC', decimalComma: false },
      )
      expect(csv).toContain(`'${name}`)
    },
  )

  it('keeps empty pages and blocks, and leaves null weight blank', () => {
    const csv = toCsv(
      {
        ...file,
        workouts: [
          ...file.workouts,
          { ...file.workouts[0], id: 2, date: '2026-10-02' },
        ],
        workoutExercises: [
          ...file.workoutExercises,
          { id: 4, workoutId: 1, exerciseId: 1, position: 2 },
        ],
        sets: [{ ...file.sets[0], weight: null }],
      },
      { timeZone: 'UTC', decimalComma: false },
    )
    const lines = csv.slice(1).split('\r\n')
    expect(lines).toHaveLength(5) // header, empty page, set, empty block, final newline
    expect(lines[1]).toContain('2026-10-02')
    expect(lines[1]).toMatch(/,{7}$/)
    expect(lines[2]).toContain(',3,no,,8')
    expect(lines[3]).toContain(',3,Sääriprässi,no,,,,')
  })

  it('writes bodyweight weight as added load, never bodyweight or zero for null', () => {
    const bodyweightFile = {
      ...file,
      exercises: [{ ...file.exercises[0], isBodyweight: true }],
    }
    expect(
      toCsv(bodyweightFile, { timeZone: 'UTC', decimalComma: false }),
    ).toContain(',yes,3,no,142.5,8')
    expect(
      toCsv(
        { ...bodyweightFile, sets: [{ ...file.sets[0], weight: null }] },
        { timeZone: 'UTC', decimalComma: false },
      ),
    ).toContain(',yes,3,no,,8')
  })
})
