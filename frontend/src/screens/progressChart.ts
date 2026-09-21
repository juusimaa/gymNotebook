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
  xLast: string
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
  const minimum = isBodyweight
    ? Math.max(0, Math.floor(low - padding))
    : Math.floor((low - padding) / 2.5) * 2.5
  const maximum = isBodyweight
    ? Math.ceil(high + padding)
    : Math.ceil((high + padding) / 2.5) * 2.5

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
    ticks: [maximum, (maximum + minimum) / 2, minimum].map((value) => ({
      key: String(value),
      y: y(value),
      label: compactNumber(value),
    })),
    points: chartPoints,
    xFirst: formatProgressShortDate(points[0].date),
    xLast: formatProgressShortDate(points.at(-1)!.date),
  }
}
