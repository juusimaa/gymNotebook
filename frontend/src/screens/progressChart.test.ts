import { describe, expect, it } from 'vitest'
import type { ExerciseHistoryPointResponse } from '../api/exercises'
import {
  buildProgressChart,
  describeProgressSession,
  formatProgressChange,
  formatProgressDate,
  formatProgressSet,
  progressDisplayChange,
} from './progressChart'

function point(
  workoutId: number,
  date: string,
  value: number,
  weight: number | null = 90,
  reps = 3,
): ExerciseHistoryPointResponse {
  return {
    workoutId,
    date,
    startedAt: `${date}T07:00:00Z`,
    weight,
    reps,
    value,
  }
}

describe('buildProgressChart', () => {
  it('keeps same-day workouts as separate chart points', () => {
    const chart = buildProgressChart(
      [point(1, '2026-09-08', 99), point(2, '2026-09-08', 100)],
      false,
    )

    expect(chart.points).toHaveLength(2)
    expect(chart.points[0].cx).toBeLessThan(chart.points[1].cx)
    expect(chart.xFirst).toBe('8.9.')
    expect(chart.xLast).toBeNull()
  })

  it('centres a single point in a non-zero snapped range', () => {
    const chart = buildProgressChart([point(1, '2026-09-08', 100)], false)

    expect(chart.points[0].cx).toBe(188)
    expect(chart.points[0].cy).toBeGreaterThan(14)
    expect(chart.points[0].cy).toBeLessThan(150)
    expect(chart.ticks.map((tick) => tick.label)).toEqual(['101', '100', '99'])
  })

  it('uses whole-rep bounds for bodyweight history', () => {
    const chart = buildProgressChart(
      [
        point(1, '2026-09-01', 8, null, 8),
        point(2, '2026-09-08', 10, null, 10),
      ],
      true,
    )

    expect(chart.ticks.map((tick) => tick.label)).toEqual([
      '11',
      '10',
      '9',
      '8',
      '7',
    ])
  })

  it('puts y-ticks on a round step instead of halving the range', () => {
    // The 2026-10-04 critique's case: a 2.5 kg snap halved into 58.8.
    const chart = buildProgressChart(
      [point(1, '2026-10-01', 58), point(2, '2026-10-04', 59.5)],
      false,
    )

    expect(chart.ticks.map((tick) => tick.label)).toEqual([
      '60',
      '59',
      '58',
      '57',
    ])
  })

  it('picks a wider round step for a wide range', () => {
    const chart = buildProgressChart(
      [point(1, '2026-09-01', 60), point(2, '2026-10-01', 100)],
      false,
    )

    expect(chart.ticks.map((tick) => tick.label)).toEqual([
      '120',
      '100',
      '80',
      '60',
      '40',
    ])
  })

  it('labels the x-axis at both ends when the dates differ', () => {
    const chart = buildProgressChart(
      [point(1, '2026-09-01', 90), point(2, '2026-10-04', 95)],
      false,
    )

    expect(chart.xFirst).toBe('1.9.')
    expect(chart.xLast).toBe('4.10.')
  })
})

describe('progress formatting', () => {
  it('formats dates, source sets and signed changes', () => {
    expect(formatProgressDate('2026-09-08')).toBe('08 Sep')
    expect(formatProgressSet(point(1, '2026-09-08', 100, 100, 1), false)).toBe(
      '100 kg × 1',
    )
    expect(formatProgressChange(4.25)).toBe('+4.3')
    expect(formatProgressChange(-2)).toBe('−2')
    expect(formatProgressChange(0)).toBe('—')
    expect(formatProgressChange(null)).toBe('first')
  })

  it('drops trailing zeros from a set weight', () => {
    expect(formatProgressSet(point(1, '2026-09-08', 87, 72.5, 6), false)).toBe(
      '72.5 kg × 6',
    )
    expect(
      formatProgressSet(point(1, '2026-09-08', 101.25, 101.25, 1), false),
    ).toBe('101.25 kg × 1')
  })

  // 94.4 and 99.9 print as 94 and 100, so the change between them must
  // print as +6, not the +5.5 between the precise estimates.
  it('measures a loaded change between the printed whole-kg figures', () => {
    expect(progressDisplayChange(94.4, 99.9, false)).toBe(6)
    expect(progressDisplayChange(85.2, 87.6, false)).toBe(3)
  })

  it('measures a bodyweight change in reps as they are', () => {
    expect(progressDisplayChange(8, 11, true)).toBe(3)
  })
})

describe('describeProgressSession', () => {
  it('names each figure of a loaded row and its gain', () => {
    expect(
      describeProgressSession(
        point(7, '2026-10-04', 116.7, 100, 5),
        2,
        false,
        '07.15',
      ),
    ).toBe(
      '04 Oct 07.15: 100 kg × 5, e1RM 117 kg, up 2 kg from the session before',
    )
  })

  it('says a single is its own weight rather than repeating it', () => {
    expect(
      describeProgressSession(
        point(7, '2026-10-04', 120, 120, 1),
        -3,
        false,
        '07.15',
      ),
    ).toBe(
      '04 Oct 07.15: 120 kg × 1, single, weight as logged, down 3 kg from the session before',
    )
  })

  it('flags a high-rep estimate and the first session', () => {
    expect(
      describeProgressSession(
        point(7, '2026-10-04', 80, 50, 15),
        null,
        false,
        '18.40',
      ),
    ).toBe(
      '04 Oct 18.40: 50 kg × 15, e1RM 80 kg, high-rep estimate, less certain, first session charted',
    )
  })

  it('counts a bodyweight row in reps without a separate figure', () => {
    expect(
      describeProgressSession(
        point(7, '2026-10-04', 9, null, 9),
        1,
        true,
        '07.15',
      ),
    ).toBe('04 Oct 07.15: 9 reps, up 1 rep from the session before')
    expect(
      describeProgressSession(
        point(8, '2026-10-05', 9, null, 9),
        0,
        true,
        '07.15',
      ),
    ).toBe('05 Oct 07.15: 9 reps, no change from the session before')
  })
})
