import { useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import {
  getExerciseHistory,
  searchExercises,
  type ExerciseHistoryResponse,
  type ExerciseResponse,
} from '../api/exercises'
import { filterExercisesByName } from './exerciseFormat'
import {
  buildProgressChart,
  describeProgressSession,
  formatProgressChange,
  formatProgressDate,
  formatProgressFigure,
  formatProgressSet,
  progressDisplayChange,
} from './progressChart'
import { formatWorkoutTime } from './workoutFormat'
import './Progress.css'
import { NotebookNav } from './NotebookNav'

export default function Progress() {
  const [exercises, setExercises] = useState<ExerciseResponse[] | null>(null)
  const [searchParams, setSearchParams] = useSearchParams()
  const [query, setQuery] = useState('')
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

  // The selection lives in the address (/progress?exercise=12), so Back from a
  // session opened in the history returns to the same exercise. An id that
  // isn't one of the caller's exercises (deleted, merged, mistyped) falls back
  // to the first one, as no id does.
  const requestedId = Number(searchParams.get('exercise'))
  const selectedExerciseId =
    exercises === null
      ? null
      : (exercises.find((exercise) => exercise.id === requestedId)?.id ??
        exercises[0]?.id ??
        null)

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
      // Replace, not push: tapping through exercises shouldn't fill Back with
      // every one of them.
      setSearchParams({ exercise: String(id) }, { replace: true })
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
        <NotebookNav current="progress" />
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
            <ExercisePicker
              exercises={exercises}
              selectedId={selectedExerciseId}
              query={query}
              onQueryChange={setQuery}
              onSelect={selectExercise}
            />

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

// A search field over the exercise chips. The chips wrap rather than scroll
// sideways (design critique 2026-10-16: a sideways row hid most of a long
// list), and the field filters the list Progress already holds, so finding one
// exercise among forty is typing a few letters rather than scrolling for it.
// The selected exercise stays selected while the filter hides its chip: the
// chart below still belongs to it.
function ExercisePicker({
  exercises,
  selectedId,
  query,
  onQueryChange,
  onSelect,
}: {
  exercises: ExerciseResponse[]
  selectedId: number | null
  query: string
  onQueryChange: (query: string) => void
  onSelect: (id: number) => void
}) {
  const selectedRef = useRef<HTMLButtonElement>(null)
  const matches = filterExercisesByName(exercises, query)

  // The chip list is capped in height (Progress.css), so an exercise chosen
  // further down, or arrived at through ?exercise=, may start out of sight.
  // "nearest" leaves it alone when it is already visible.
  useEffect(() => {
    selectedRef.current?.scrollIntoView({ block: 'nearest' })
  }, [selectedId])

  return (
    <div className="progress-picker">
      <input
        className="input"
        type="search"
        autoComplete="off"
        aria-label="Find an exercise"
        placeholder="Find an exercise"
        value={query}
        onChange={(event) => onQueryChange(event.target.value)}
      />
      {matches.length > 0 && (
        // role="group": an aria-label on a plain div isn't announced.
        <div
          className="progress-picker-chips"
          role="group"
          aria-label="Exercise"
        >
          {matches.map((exercise) => (
            <button
              className={exercise.id === selectedId ? 'is-selected' : undefined}
              type="button"
              aria-pressed={exercise.id === selectedId}
              onClick={() => onSelect(exercise.id)}
              ref={exercise.id === selectedId ? selectedRef : undefined}
              key={exercise.id}
            >
              {exercise.name}
            </button>
          ))}
        </div>
      )}
      {/* Always in the page, so the screen reader hears it when it fills. */}
      <p className="progress-picker-status" role="status">
        {matches.length === 0 && `No exercise matches “${query.trim()}”.`}
      </p>
    </div>
  )
}

function ProgressHistory({ history }: { history: ExerciseHistoryResponse }) {
  const { isBodyweight, points } = history
  const metricLabel = isBodyweight
    ? 'Best reps per session'
    : 'Estimated strength trend (e1RM), best per session'
  const metricUnit = isBodyweight ? 'reps' : 'kg'

  if (points.length === 0) {
    return (
      <section className="progress-no-points">
        <h2>{history.exerciseName}</h2>
        <ProgressMetric isBodyweight={isBodyweight} unit={metricUnit} />
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
  // One session has nothing to change from, so its Change says so rather
  // than printing "—" since its own date.
  const totalChange =
    points.length === 1
      ? null
      : progressDisplayChange(first.value, latest.value, isBodyweight)
  const newestFirst = points
    .map((point, index) => ({
      point,
      change:
        index === 0
          ? null
          : progressDisplayChange(
              points[index - 1].value,
              point.value,
              isBodyweight,
            ),
    }))
    .reverse()

  return (
    <>
      <section className="progress-title">
        <h2>{history.exerciseName}</h2>
        <ProgressMetric isBodyweight={isBodyweight} unit={metricUnit} />
      </section>

      <figure className="progress-chart">
        <div className="progress-chart-plot">
          <svg
            viewBox={`0 0 ${chart.width} ${chart.height}`}
            role="img"
            aria-labelledby="progress-chart-title progress-chart-description"
          >
            <title id="progress-chart-title">
              {history.exerciseName},{' '}
              {/* Lower-case only the first letter, so it stays "e1RM". */}
              {metricLabel.charAt(0).toLowerCase() + metricLabel.slice(1)}
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
        <figcaption
          className={`progress-chart-x num${chart.xLast === null ? ' is-single' : ''}`}
        >
          <span>{chart.xFirst}</span>
          {chart.xLast !== null && <span>{chart.xLast}</span>}
        </figcaption>
      </figure>

      <section className="progress-stats" aria-label="Progress summary">
        <ProgressStat
          label="Latest"
          figure={formatProgressFigure(latest.value, isBodyweight)}
          unit={metricUnit}
          meta={`${formatProgressDate(latest.date)} · ${formatProgressSet(latest, isBodyweight)}`}
        />
        <ProgressStat
          label="Best"
          figure={formatProgressFigure(best.value, isBodyweight)}
          unit={metricUnit}
          meta={`${formatProgressDate(best.date)}${best.reps === 1 && !isBodyweight ? ' · single' : ''}`}
        />
        <ProgressStat
          label="Change"
          figure={
            totalChange === null ? '—' : formatProgressChange(totalChange)
          }
          unit={totalChange ? metricUnit : undefined}
          meta={
            totalChange === null
              ? 'one session so far'
              : `since ${formatProgressDate(first.date)}`
          }
          accent
        />
      </section>

      <div className="hr progress-divider" aria-hidden="true"></div>

      {/* Each row opens the session it came from. Its aria-label reads the
          row as a sentence (describeProgressSession); the columns alone are
          bare figures to a screen reader. */}
      <ul className="progress-sessions" aria-label="Session history">
        {newestFirst.map(({ point, change }) => {
          const time = formatWorkoutTime(point.startedAt)
          return (
            <li key={point.workoutId}>
              <Link
                className="progress-session-row num"
                to={`/workouts/${point.workoutId}`}
                aria-label={describeProgressSession(
                  point,
                  change,
                  isBodyweight,
                  time,
                )}
              >
                <div className="progress-session-date">
                  <span>{formatProgressDate(point.date)}</span>
                  <span>{time}</span>
                </div>
                <div className="progress-session-set">
                  {formatProgressSet(point, isBodyweight)}
                  {point.reps === 1 && !isBodyweight && (
                    <span>single — weight as logged</span>
                  )}
                  {point.reps > 12 && !isBodyweight && (
                    <span>High-rep estimate · less certain</span>
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
              </Link>
            </li>
          )
        })}
      </ul>

      {isBodyweight ? (
        <p className="progress-caveat">
          Bodyweight exercise — the y-axis is reps. Belt-loaded sets are not
          charted here.
        </p>
      ) : (
        // One sentence in view and the rest folded (design critique
        // 2026-10-16: six sentences in one paragraph went unread). The
        // guidance itself is unchanged; PLAN.md's e1RM rules require it.
        <div className="progress-guidance">
          <p className="progress-caveat">
            Estimated one-rep max (e1RM) from your best working set each
            session: a strength trend, not a test.
          </p>
          <details className="progress-details">
            <summary>How to read this estimate</summary>
            <ul>
              <li>
                Compare the same exercise and equipment, with similar technique,
                rep ranges and effort.
              </li>
              <li>
                Leaving more reps in reserve can lower the estimate without a
                loss of strength.
              </li>
              <li>
                Estimates become less reliable at higher reps, especially above
                roughly 12; small changes may not reflect strength gains.
              </li>
              <li>
                A single shows the weight lifted, which may not be your maximum.
              </li>
            </ul>
            <p>
              <a href="https://pmc.ncbi.nlm.nih.gov/articles/PMC10933212/">
                Research on repetitions and maximum strength
              </a>
            </p>
          </details>
        </div>
      )}
    </>
  )
}

// The metric line is set in tracked capitals, which would print "E1RM". The
// term is kept in its own case so it reads as the abbreviation it is.
function ProgressMetric({
  isBodyweight,
  unit,
}: {
  isBodyweight: boolean
  unit: string
}) {
  return (
    <p className="progress-metric">
      {isBodyweight ? (
        'Best reps per session'
      ) : (
        <>
          Estimated strength trend (
          <span className="progress-metric-term">e1RM</span>) · best per session
        </>
      )}{' '}
      · {unit}
    </p>
  )
}

// A headline figure carries its unit beside it, so "99" can't be read as
// anything but 99 kg (or reps). A change of nothing ("—") has no unit.
function ProgressStat({
  label,
  figure,
  unit,
  meta,
  accent = false,
}: {
  label: string
  figure: string
  unit?: string
  meta: string
  accent?: boolean
}) {
  return (
    <div className={accent ? 'is-accent' : undefined}>
      <p>{label}</p>
      <strong className="num">
        {figure}
        {unit !== undefined && <small> {unit}</small>}
      </strong>
      <span>{meta}</span>
    </div>
  )
}
