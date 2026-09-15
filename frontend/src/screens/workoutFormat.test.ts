import { describe, expect, it } from 'vitest'
import {
  formatCount,
  formatWorkoutDate,
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
