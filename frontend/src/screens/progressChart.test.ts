import { describe, expect, it } from 'vitest'
import type { ExerciseHistoryPointResponse } from '../api/exercises'
import {
  buildProgressChart,
  formatProgressChange,
  formatProgressDate,
  formatProgressSet,
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
    expect(chart.xLast).toBe('8.9.')
  })

  it('centres a single point in a non-zero snapped range', () => {
    const chart = buildProgressChart([point(1, '2026-09-08', 100)], false)

    expect(chart.points[0].cx).toBe(188)
    expect(chart.points[0].cy).toBeGreaterThan(14)
    expect(chart.points[0].cy).toBeLessThan(150)
    expect(chart.ticks.map((tick) => tick.label)).toEqual([
      '102.5',
      '100',
      '97.5',
    ])
  })

  it('uses whole-rep bounds for bodyweight history', () => {
    const chart = buildProgressChart(
      [
        point(1, '2026-09-01', 8, null, 8),
        point(2, '2026-09-08', 10, null, 10),
      ],
      true,
    )

    expect(chart.ticks.map((tick) => tick.label)).toEqual(['11', '9', '7'])
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
})
