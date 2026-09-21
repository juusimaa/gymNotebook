import { describe, expect, it } from 'vitest'
import {
  formatBestSet,
  formatCount,
  formatWorkoutDate,
  formatWorkoutLongDate,
  formatSetLoad,
  formatWorkoutTime,
} from './workoutFormat'

describe('formatWorkoutDate', () => {
  // A workout date is the calendar date the user chose, not an instant. This
  // assertion protects against changing the helper to `new Date(date)`, which
  // could move the day backwards when formatted in a timezone west of UTC.
  it('returns the day and abbreviated month without timezone conversion', () => {
    expect(formatWorkoutDate('2026-09-08')).toEqual({
      day: '08',
      month: 'Sep',
    })
  })
})

describe('formatWorkoutLongDate', () => {
  it('formats the selected calendar date without timezone conversion', () => {
    expect(formatWorkoutLongDate('2026-09-08')).toBe('8 September 2026')
  })
})

describe('formatWorkoutTime', () => {
  // Helsinki changes between UTC+2 and UTC+3. Covering both seasons proves the
  // IANA timezone performs the daylight-saving conversion while en-FI supplies
  // the Finnish HH.mm presentation.
  it.each([
    ['summer time', '2026-09-08T04:15:00Z', '07.15'],
    ['winter time', '2026-01-08T05:15:00Z', '07.15'],
  ])('formats %s in the Finnish convention', (_, timestamp, expected) => {
    expect(formatWorkoutTime(timestamp, 'Europe/Helsinki')).toBe(expected)
  })
})

describe('formatCount', () => {
  it.each([
    [0, 'exercise', '0 exercises'],
    [1, 'exercise', '1 exercise'],
    [2, 'set', '2 sets'],
  ])('formats %i as %s', (count, singular, expected) => {
    expect(formatCount(count, singular)).toBe(expected)
  })
})

describe('formatSetLoad', () => {
  it.each([
    [false, 90, 3, '90 kg × 3'],
    [true, null, 8, 'bodyweight × 8'],
    [true, 10, 5, '+10 kg × 5'],
  ])(
    'formats bodyweight=%s weight=%s reps=%s',
    (isBodyweight, weight, reps, expected) => {
      expect(formatSetLoad(isBodyweight, weight, reps)).toBe(expected)
    },
  )
})

describe('formatBestSet', () => {
  it('excludes warm-ups when selecting the best loaded set', () => {
    expect(
      formatBestSet(false, [
        { weight: 120, reps: 5, isWarmup: true },
        { weight: 90, reps: 3, isWarmup: false },
      ]),
    ).toBe('e1RM 99 kg')
  })

  it('uses a tested single without applying the Epley estimate', () => {
    expect(
      formatBestSet(false, [
        { weight: 100, reps: 1, isWarmup: false },
        { weight: 90, reps: 3, isWarmup: false },
      ]),
    ).toBe('e1RM 100 kg')
  })

  it('uses working-set reps for a bodyweight exercise', () => {
    expect(
      formatBestSet(true, [
        { weight: null, reps: 12, isWarmup: true },
        { weight: null, reps: 8, isWarmup: false },
        { weight: 10, reps: 5, isWarmup: false },
      ]),
    ).toBe('best 8 reps')
  })
})
