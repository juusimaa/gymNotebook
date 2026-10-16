import { describe, expect, it } from 'vitest'
import {
  formatBestSet,
  formatCount,
  formatInstantDate,
  formatPageHeadline,
  formatWorkoutDate,
  formatWorkoutLongDate,
  formatSetCounts,
  formatSetLoad,
  formatWorkoutTime,
  formatWorkoutTimeRange,
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

describe('formatWorkoutTimeRange', () => {
  // Times are Helsinki summer time (UTC+3).
  it('shows only the times when the session ends on its start day', () => {
    expect(
      formatWorkoutTimeRange(
        '2026-09-30T04:15:00Z',
        '2026-09-30T05:40:00Z',
        'Europe/Helsinki',
      ),
    ).toBe('07.15–08.40')
  })

  it('adds the end date when the session ends after midnight', () => {
    expect(
      formatWorkoutTimeRange(
        '2026-09-15T20:30:00Z',
        '2026-09-15T21:15:00Z',
        'Europe/Helsinki',
      ),
    ).toBe('23.30–00.15 16 Sep')
  })

  it('adds the end date when the page was finished days later', () => {
    expect(
      formatWorkoutTimeRange(
        '2026-09-30T04:15:00Z',
        '2026-10-07T09:06:00Z',
        'Europe/Helsinki',
      ),
    ).toBe('07.15–12.06 7 Oct')
  })

  it('adds the year when the end falls in another year', () => {
    expect(
      formatWorkoutTimeRange(
        '2026-12-31T20:30:00Z',
        '2026-12-31T22:30:00Z',
        'Europe/Helsinki',
      ),
    ).toBe('22.30–00.30 1 Jan 2027')
  })
})

describe('formatInstantDate', () => {
  it('writes the day in English whatever the browser locale', () => {
    // Midday UTC, so the day is the same in any time zone the test runs in.
    expect(formatInstantDate('2026-10-04T12:00:00Z')).toBe('4 October 2026')
  })
})

describe('formatPageHeadline', () => {
  it('formatPageHeadline_WithTitle_ReturnsTitle', () => {
    expect(formatPageHeadline('Push A', ['Bench Press'])).toBe('Push A')
  })

  it('formatPageHeadline_Untitled_JoinsExercisesInOrder', () => {
    expect(formatPageHeadline(null, ['Bench Press', 'Pull-up'])).toBe(
      'Bench Press · Pull-up',
    )
  })

  it('formatPageHeadline_RepeatedExercise_NamesItOnce', () => {
    expect(
      formatPageHeadline(null, ['Bench Press', 'Pull-up', 'Bench Press']),
    ).toBe('Bench Press · Pull-up')
  })

  it('formatPageHeadline_BlankTitle_FallsBackToExercises', () => {
    expect(formatPageHeadline('  ', ['Deadlift'])).toBe('Deadlift')
  })

  it('formatPageHeadline_UntitledWithoutExercises_SaysSo', () => {
    expect(formatPageHeadline(null, [])).toBe('No exercises logged')
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

  // specs/006: only done sets count, as on the progress chart.
  it('ignores planned and skipped sets', () => {
    // Arrange
    const sets = [
      { weight: 140, reps: 5, isWarmup: false, status: 'skipped' as const },
      { weight: 130, reps: 5, isWarmup: false, status: 'planned' as const },
      { weight: 90, reps: 3, isWarmup: false, status: 'done' as const },
    ]

    // Act
    const best = formatBestSet(false, sets)

    // Assert
    expect(best).toBe('e1RM 99 kg')
  })

  it('says so when no set is done yet', () => {
    // Arrange
    const sets = [
      { weight: 100, reps: 5, isWarmup: false, status: 'planned' as const },
      { weight: 60, reps: 8, isWarmup: true, status: 'skipped' as const },
    ]

    // Act
    const best = formatBestSet(false, sets)

    // Assert
    expect(best).toBe('no sets done')
  })

  it('reads warm-ups only when only warm-ups are done', () => {
    // Arrange
    const sets = [
      { weight: 60, reps: 8, isWarmup: true, status: 'done' as const },
      { weight: 100, reps: 5, isWarmup: false, status: 'skipped' as const },
    ]

    // Act
    const best = formatBestSet(false, sets)

    // Assert
    expect(best).toBe('warm-ups only')
  })
})

describe('formatSetCounts', () => {
  it('reads as before when every set is done', () => {
    expect(formatSetCounts(9, 0, false)).toBe('9 sets')
    expect(formatSetCounts(1, 0, true)).toBe('1 set')
  })

  it('adds the skipped sets on a finished page', () => {
    expect(formatSetCounts(9, 2, false)).toBe('9 sets · 2 skipped')
  })

  it('says how far along a page in progress is', () => {
    expect(formatSetCounts(4, 5, true)).toBe('4 of 9 done')
  })
})
