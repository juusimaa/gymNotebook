import { useEffect, useState } from 'react'
import { listWorkouts, type WorkoutSummaryResponse } from '../api/workouts'
import { Link } from 'react-router'
import {
  formatCount,
  formatWorkoutDate,
  formatWorkoutTime,
} from './workoutFormat'
import './Sessions.css'

const PAGE_SIZE = 20

export default function Sessions() {
  // `null` is the loading sentinel. Once the request succeeds this becomes an
  // array, including an empty array when the user has not logged a session yet.
  const [workouts, setWorkouts] = useState<WorkoutSummaryResponse[] | null>(
    null,
  )
  // Request failures are kept separate from the data so the empty state cannot
  // be mistaken for a failed request.
  const [message, setMessage] = useState<string | null>(null)
  // Incrementing this value retries the first-page request. Keeping the retry
  // trigger as state lets the effect retain ownership of its cancellation flag.
  const [loadAttempt, setLoadAttempt] = useState(0)
  const [hasEarlierPages, setHasEarlierPages] = useState(false)
  const [loadingEarlier, setLoadingEarlier] = useState(false)
  const [earlierMessage, setEarlierMessage] = useState<string | null>(null)

  // Effects cannot themselves be async because their return value is reserved
  // for cleanup, so the request lives in an inner function and is launched below.
  useEffect(() => {
    let cancelled = false

    async function load() {
      setMessage(null)

      try {
        const response = await listWorkouts(PAGE_SIZE)

        // React Strict Mode mounts effects twice in development, and navigation
        // may unmount this screen while fetch is pending. Ignore either stale
        // response rather than updating a component that is no longer current.
        if (!cancelled) {
          setWorkouts(response)
          setHasEarlierPages(response.length === PAGE_SIZE)
        }
      } catch {
        if (!cancelled) {
          setMessage('The notebook could not be opened. Please try again.')
        }
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [loadAttempt])

  function retry() {
    // Return to the loading state immediately. The increment changes the
    // effect dependency, which starts a fresh request with a new cleanup flag.
    setWorkouts(null)
    setMessage(null)
    setLoadAttempt((attempt) => attempt + 1)
  }

  async function loadEarlier() {
    // Do not start another request while the current page is already loading.
    if (workouts === null || loadingEarlier) {
      return
    }

    const lastWorkout = workouts.at(-1)

    if (lastWorkout === undefined) {
      return
    }

    setLoadingEarlier(true)
    setEarlierMessage(null)

    try {
      // The final displayed id is the keyset cursor: the API returns workouts
      // ordered immediately before that row rather than using a page number.
      const response = await listWorkouts(PAGE_SIZE, lastWorkout.id)

      // Never mutate the existing array with push. A new array reference tells
      // React that state changed and preserves all pages already on screen.
      setWorkouts((current) =>
        current === null ? response : [...current, ...response],
      )
      setHasEarlierPages(response.length === PAGE_SIZE)
    } catch {
      // Keep the workouts already loaded; only the request for older rows failed.
      setEarlierMessage('Earlier pages could not be loaded. Please try again.')
    } finally {
      setLoadingEarlier(false)
    }
  }

  // Failures are announced by assistive technology when they appear. Keeping
  // Retry as a real button also makes it available to keyboard users.
  if (message !== null) {
    return (
      <main className="page">
        <p className="form-message" role="alert">
          {message}
        </p>
        <button className="btn btn-secondary" type="button" onClick={retry}>
          Try again
        </button>
      </main>
    )
  }

  if (workouts === null) {
    return <main className="page">Opening pages…</main>
  }

  return (
    <main className="page sessions">
      <header className="sessions-header">
        <div>
          <p className="kicker">Pages</p>
          <h1>Sessions</h1>
        </div>

        <nav className="sessions-nav" aria-label="Notebook">
          <Link to="/progress">Progress</Link>
          <Link to="/exercises">Exercises</Link>
          <Link to="/">Cover</Link>
        </nav>
      </header>

      <div className="sessions-list">
        {workouts.length === 0 ? (
          <p className="sessions-empty">
            No sessions logged yet. Start a new page when you’re ready.
          </p>
        ) : (
          // A page is one session, not one day. Map every response row directly
          // so two workouts on the same calendar date remain separate entries.
          workouts.map((workout) => {
            // `date` is the user's calendar date and must not pass through Date;
            // startedAt/endedAt are instants and do need local-time formatting.
            const { day, month } = formatWorkoutDate(workout.date)
            const startedAt = formatWorkoutTime(workout.startedAt)
            const timeRange =
              workout.endedAt === null
                ? `${startedAt}–in progress`
                : `${startedAt}–${formatWorkoutTime(workout.endedAt)}`

            const exerciseSummary =
              workout.exerciseNames.length === 0
                ? 'No exercises logged'
                : workout.exerciseNames.join(' · ')

            return (
              // The whole row is one link, giving mouse, touch, and keyboard
              // users a single generous target for opening the session page.
              <Link
                className="session-row"
                to={`/workouts/${workout.id}`}
                key={workout.id}
              >
                <div className="session-date num">
                  <span className="session-day">{day}</span>
                  <span className="session-month">{month}</span>
                </div>

                <div className="session-content">
                  <div className="session-heading">
                    <h2>{workout.title ?? 'Untitled session'}</h2>
                    <span className="session-time num">{startedAt}</span>
                  </div>

                  <p className="session-exercises">{exerciseSummary}</p>

                  <p className="session-meta num">
                    {formatCount(workout.exerciseCount, 'exercise')} ·{' '}
                    {formatCount(workout.setCount, 'set')} · {timeRange}
                  </p>
                </div>
              </Link>
            )
          })
        )}
        {hasEarlierPages && workouts.length > 0 && (
          <button
            className="btn btn-ghost sessions-earlier"
            type="button"
            disabled={loadingEarlier}
            aria-busy={loadingEarlier}
            onClick={() => void loadEarlier()}
          >
            {loadingEarlier ? 'Opening earlier pages…' : 'Earlier pages'}
          </button>
        )}

        {earlierMessage !== null && (
          <p className="form-message sessions-earlier-message" role="alert">
            {earlierMessage}
          </p>
        )}
      </div>

      <footer className="sessions-action">
        <Link className="btn btn-primary btn-block" to="/workouts/new">
          Start a new page
        </Link>
      </footer>
    </main>
  )
}
