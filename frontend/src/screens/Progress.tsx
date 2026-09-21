import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import {
  getExerciseHistory,
  searchExercises,
  type ExerciseHistoryResponse,
  type ExerciseResponse,
} from '../api/exercises'
import {
  buildProgressChart,
  formatProgressChange,
  formatProgressDate,
  formatProgressFigure,
  formatProgressSet,
} from './progressChart'
import { formatWorkoutTime } from './workoutFormat'
import './Progress.css'

export default function Progress() {
  const [exercises, setExercises] = useState<ExerciseResponse[] | null>(null)
  const [selectedExerciseId, setSelectedExerciseId] = useState<number | null>(
    null,
  )
  const [history, setHistory] = useState<ExerciseHistoryResponse | null>(null)
  const [exerciseMessage, setExerciseMessage] = useState<string | null>(null)
  const [historyMessage, setHistoryMessage] = useState<string | null>(null)
  const [exerciseLoadAttempt, setExerciseLoadAttempt] = useState(0)
  const [historyLoadAttempt, setHistoryLoadAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false

    async function loadExercises() {
      setExerciseMessage(null)

      try {
        const response = await searchExercises('')
        if (!cancelled) {
          setExercises(response)
          setSelectedExerciseId((current) => current ?? response[0]?.id ?? null)
        }
      } catch {
        if (!cancelled) {
          setExerciseMessage(
            'The exercise index could not be opened. Please try again.',
          )
        }
      }
    }

    void loadExercises()
    return () => {
      cancelled = true
    }
  }, [exerciseLoadAttempt])

  useEffect(() => {
    if (selectedExerciseId === null) {
      return
    }

    const exerciseId = selectedExerciseId
    let cancelled = false

    async function loadHistory() {
      try {
        const response = await getExerciseHistory(exerciseId)
        if (!cancelled) {
          setHistory(response)
        }
      } catch {
        if (!cancelled) {
          setHistoryMessage(
            'This exercise history could not be opened. Please try again.',
          )
        }
      }
    }

    void loadHistory()
    return () => {
      cancelled = true
    }
  }, [historyLoadAttempt, selectedExerciseId])

  function retryExercises() {
    setExercises(null)
    setExerciseMessage(null)
    setExerciseLoadAttempt((attempt) => attempt + 1)
  }

  function retryHistory() {
    setHistory(null)
    setHistoryMessage(null)
    setHistoryLoadAttempt((attempt) => attempt + 1)
  }

  function selectExercise(id: number) {
    if (id !== selectedExerciseId) {
      setHistory(null)
      setHistoryMessage(null)
      setSelectedExerciseId(id)
    }
  }

  if (exerciseMessage !== null) {
    return (
      <main className="page progress-state">
        <p className="form-message" role="alert">
          {exerciseMessage}
        </p>
        <button
          className="btn btn-secondary"
          type="button"
          onClick={retryExercises}
        >
          Try again
        </button>
        <Link className="btn btn-ghost" to="/workouts">
          Back to sessions
        </Link>
      </main>
    )
  }

  if (exercises === null) {
    return <main className="page progress-state">Opening chart…</main>
  }

  return (
    <main className="page progress">
      <header className="progress-header">
        <div>
          <p className="kicker">Chart</p>
          <h1>Progress</h1>
        </div>
        <nav className="progress-nav" aria-label="Notebook">
          <Link to="/workouts">Sessions</Link>
          <span aria-disabled="true">Exercises</span>
        </nav>
      </header>

      <div className="progress-content">
        {exercises.length === 0 ? (
          <section className="progress-empty">
            <h2>No exercises yet</h2>
            <p className="muted">
              Log a working set first, then its progress will appear here.
            </p>
            <Link className="btn btn-primary" to="/workouts/new">
              Start a new page
            </Link>
          </section>
        ) : (
          <>
            <div className="progress-picker" aria-label="Exercise">
              {exercises.map((exercise) => (
                <button
                  className={
                    exercise.id === selectedExerciseId
                      ? 'is-selected'
                      : undefined
                  }
                  type="button"
                  aria-pressed={exercise.id === selectedExerciseId}
                  onClick={() => selectExercise(exercise.id)}
                  key={exercise.id}
                >
                  {exercise.name}
                </button>
              ))}
            </div>

            {historyMessage !== null ? (
              <section className="progress-inline-state">
                <p className="form-message" role="alert">
                  {historyMessage}
                </p>
                <button
                  className="btn btn-secondary"
                  type="button"
                  onClick={retryHistory}
                >
                  Try again
                </button>
              </section>
            ) : history === null ? (
              <p className="progress-inline-state">Drawing chart…</p>
            ) : (
              <ProgressHistory history={history} />
            )}
          </>
        )}
      </div>
    </main>
  )
}

function ProgressHistory({ history }: { history: ExerciseHistoryResponse }) {
  const { isBodyweight, points } = history
  const metricLabel = isBodyweight
    ? 'Best reps per session'
    : 'Best e1RM per session'
  const metricUnit = isBodyweight ? 'reps' : 'kg'

  if (points.length === 0) {
    return (
      <section className="progress-no-points">
        <h2>{history.exerciseName}</h2>
        <p className="progress-metric">
          {metricLabel} · {metricUnit}
        </p>
        <p className="muted">
          No qualifying working sets yet. Warm-ups, loaded sets without a
          weight, and added-weight bodyweight sets do not become chart points.
        </p>
      </section>
    )
  }

  const chart = buildProgressChart(points, isBodyweight)
  const first = points[0]
  const latest = points.at(-1)!
  const best = points.reduce((current, point) =>
    point.value > current.value ? point : current,
  )
  const totalChange = latest.value - first.value
  const newestFirst = points
    .map((point, index) => ({
      point,
      change: index === 0 ? null : point.value - points[index - 1].value,
    }))
    .reverse()

  return (
    <>
      <section className="progress-title">
        <h2>{history.exerciseName}</h2>
        <p className="progress-metric">
          {metricLabel} · {metricUnit}
        </p>
      </section>

      <figure className="progress-chart">
        <div className="progress-chart-plot">
          <svg
            viewBox={`0 0 ${chart.width} ${chart.height}`}
            role="img"
            aria-labelledby="progress-chart-title progress-chart-description"
          >
            <title id="progress-chart-title">
              {history.exerciseName} {metricLabel.toLowerCase()}
            </title>
            <desc id="progress-chart-description">
              {points.length} session points from{' '}
              {formatProgressDate(first.date)}
              {' to '}
              {formatProgressDate(latest.date)}.
            </desc>
            {chart.ticks.map((tick) => (
              <line
                x1={chart.plotLeft}
                y1={tick.y}
                x2={chart.plotRight}
                y2={tick.y}
                className="progress-chart-grid"
                key={tick.key}
              />
            ))}
            {chart.points.length > 1 && (
              <polyline points={chart.line} className="progress-chart-line" />
            )}
            {chart.points.map((point) => (
              <circle
                cx={point.cx}
                cy={point.cy}
                r="3"
                className="progress-chart-point"
                key={point.key}
              />
            ))}
          </svg>
          {chart.ticks.map((tick) => (
            <span
              className="progress-chart-y num"
              style={{ top: `${(tick.y / chart.height) * 100}%` }}
              aria-hidden="true"
              key={tick.key}
            >
              {tick.label}
            </span>
          ))}
        </div>
        <figcaption className="progress-chart-x num">
          <span>{chart.xFirst}</span>
          <span>{chart.xLast}</span>
        </figcaption>
      </figure>

      <section className="progress-stats" aria-label="Progress summary">
        <ProgressStat
          label="Latest"
          figure={formatProgressFigure(latest.value, isBodyweight)}
          meta={`${formatProgressDate(latest.date)} · ${formatProgressSet(latest, isBodyweight)}`}
        />
        <ProgressStat
          label="Best"
          figure={formatProgressFigure(best.value, isBodyweight)}
          meta={`${formatProgressDate(best.date)}${best.reps === 1 && !isBodyweight ? ' · tested single' : ''}`}
        />
        <ProgressStat
          label="Change"
          figure={formatProgressChange(totalChange)}
          meta={`since ${formatProgressDate(first.date)}`}
          accent
        />
      </section>

      <div className="hr progress-divider" aria-hidden="true"></div>

      <section className="progress-sessions" aria-label="Session history">
        {newestFirst.map(({ point, change }) => (
          <div className="progress-session-row num" key={point.workoutId}>
            <div className="progress-session-date">
              <span>{formatProgressDate(point.date)}</span>
              <span>{formatWorkoutTime(point.startedAt)}</span>
            </div>
            <div className="progress-session-set">
              {formatProgressSet(point, isBodyweight)}
              {point.reps === 1 && !isBodyweight && (
                <span>tested single — weight as logged</span>
              )}
            </div>
            <div className="progress-session-figure">
              {formatProgressFigure(point.value, isBodyweight)}
            </div>
            <div
              className={`progress-session-change${change !== null && change > 0 ? ' is-gain' : ''}`}
            >
              {formatProgressChange(change)}
            </div>
          </div>
        ))}
      </section>

      <p className="progress-caveat">
        {isBodyweight
          ? 'Bodyweight exercise — the y-axis is reps. Belt-loaded sets are not charted here.'
          : 'Estimates degrade above roughly 12 reps; high-rep accessory work is drawn but not worth reading closely.'}
      </p>
    </>
  )
}

function ProgressStat({
  label,
  figure,
  meta,
  accent = false,
}: {
  label: string
  figure: string
  meta: string
  accent?: boolean
}) {
  return (
    <div className={accent ? 'is-accent' : undefined}>
      <p>{label}</p>
      <strong className="num">{figure}</strong>
      <span>{meta}</span>
    </div>
  )
}
