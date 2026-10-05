import type { ExerciseHistoryPointResponse } from '../api/exercises'
import { formatWorkoutDate } from './workoutFormat'

const WIDTH = 346
const HEIGHT = 176
const LEFT = 38
const RIGHT = 8
const TOP = 14
const BOTTOM = 26

export interface ProgressChartGeometry {
  width: number
  height: number
  plotLeft: number
  plotRight: number
  line: string
  ticks: { key: string; y: number; label: string }[]
  points: { key: number; cx: number; cy: number }[]
  xFirst: string
  // null when the first and last points share a date (one session, or two
  // pages on the same day): the axis then shows that date once, centred.
  xLast: string | null
}

function compactNumber(value: number, maximumFractionDigits = 1): string {
  return value.toFixed(maximumFractionDigits).replace(/\.0+$/, '')
}

export function formatProgressFigure(
  value: number,
  isBodyweight: boolean,
): string {
  // The chart retains the precise server value for geometry and deltas, while
  // headline figures follow the prototype's whole kg / whole reps treatment.
  return compactNumber(isBodyweight ? value : Math.round(value))
}

export function formatProgressChange(change: number | null): string {
  if (change === null) {
    return 'first'
  }
  if (change === 0) {
    return '—'
  }

  const sign = change > 0 ? '+' : '−'
  return `${sign}${compactNumber(Math.abs(change))}`
}

export function formatProgressDate(date: string): string {
  const { day, month } = formatWorkoutDate(date)
  return `${day} ${month}`
}

export function formatProgressShortDate(date: string): string {
  const [, month, day] = date.split('-')
  return `${Number(day)}.${Number(month)}.`
}

export function formatProgressSet(
  point: ExerciseHistoryPointResponse,
  isBodyweight: boolean,
): string {
  if (isBodyweight) {
    return `${point.reps} ${point.reps === 1 ? 'rep' : 'reps'}`
  }

  return `${compactNumber(point.weight ?? 0, 2)} kg × ${point.reps}`
}

// The smallest "round" step — 1, 2 or 5 times a power of ten, and never under
// one whole kg or rep — that covers `raw`. Ticks on such a step read as
// figures someone would write down (58 / 59 / 60), not as arithmetic
// (57.5 / 58.8 / 60).
function niceStep(raw: number): number {
  const magnitude = 10 ** Math.floor(Math.log10(Math.max(raw, 1)))
  const step =
    [1, 2, 5, 10].map((factor) => factor * magnitude).find((s) => s >= raw) ??
    10 * magnitude
  return Math.max(step, 1)
}

export function buildProgressChart(
  points: ExerciseHistoryPointResponse[],
  isBodyweight: boolean,
): ProgressChartGeometry {
  if (points.length === 0) {
    throw new Error('A progress chart needs at least one point.')
  }

  const values = points.map((point) => point.value)
  const low = Math.min(...values)
  const high = Math.max(...values)
  const padding = (high - low || (isBodyweight ? 2 : 5)) * 0.12
  // Aim for about three intervals; snapping both ends outwards to the step
  // can add one more. Fewer, wider steps would leave the line squeezed into
  // the middle of the plot.
  const step = niceStep((high - low + 2 * padding) / 3)
  const minimum = Math.max(
    isBodyweight ? 0 : -Infinity,
    Math.floor((low - padding) / step) * step,
  )
  const maximum = Math.ceil((high + padding) / step) * step
  const tickValues: number[] = []
  for (let value = maximum; value >= minimum; value -= step) {
    tickValues.push(value)
  }

  const x = (index: number) =>
    LEFT +
    (points.length === 1
      ? (WIDTH - LEFT - RIGHT) / 2
      : (index * (WIDTH - LEFT - RIGHT)) / (points.length - 1))
  const y = (value: number) =>
    TOP +
    (1 - (value - minimum) / (maximum - minimum)) * (HEIGHT - TOP - BOTTOM)

  const chartPoints = points.map((point, index) => ({
    key: point.workoutId,
    cx: x(index),
    cy: y(point.value),
  }))

  return {
    width: WIDTH,
    height: HEIGHT,
    plotLeft: LEFT,
    plotRight: WIDTH - RIGHT,
    line: chartPoints.map((point) => `${point.cx},${point.cy}`).join(' '),
    ticks: tickValues.map((value) => ({
      key: String(value),
      y: y(value),
      label: compactNumber(value),
    })),
    points: chartPoints,
    xFirst: formatProgressShortDate(points[0].date),
    xLast:
      points[0].date === points.at(-1)!.date
        ? null
        : formatProgressShortDate(points.at(-1)!.date),
  }
}
