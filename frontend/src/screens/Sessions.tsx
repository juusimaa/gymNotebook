import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { ApiError } from '../api/client'
import {
  ALREADY_STARTED,
  listPreparedWorkouts,
  listWorkouts,
  startWorkout,
  type PreparedWorkoutSummaryResponse,
  type WorkoutSummaryResponse,
} from '../api/workouts'
import { Link, useNavigate } from 'react-router'
import { createStartTimes } from './newWorkoutDraft'
import { useRouteNotice } from './routeNotice'
import {
  formatCount,
  formatSetCounts,
  formatWorkoutDate,
  formatWorkoutTime,
  formatWorkoutTimeRange,
} from './workoutFormat'
import './Sessions.css'

const PAGE_SIZE = 20

export default function Sessions() {
  // "Page saved" or "Page torn out" from the screen that sent us here.
  const notice = useRouteNotice()
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

  // The Planned group (specs/006): pages written ahead, not started yet. Its
  // own failure stays inside the group, so a hiccup there never hides the
  // history below it. null is "not loaded", as with workouts.
  const [plans, setPlans] = useState<PreparedWorkoutSummaryResponse[] | null>(
    null,
  )
  const [plansMessage, setPlansMessage] = useState<string | null>(null)
  // The plan whose Start is on its way, and what went wrong with the last one.
  const [startingPlanId, setStartingPlanId] = useState<number | null>(null)
  const [startMessage, setStartMessage] = useState<string | null>(null)
  const navigate = useNavigate()

  // "Plan a session" opens a small chooser above the footer rather than a
  // modal. Opening it moves focus to its first choice; closing it (Close or
  // Escape) hands focus back to the button that opened it.
  const [choosingPlan, setChoosingPlan] = useState(false)
  const planButtonRef = useRef<HTMLButtonElement>(null)
  const firstChoiceRef = useRef<HTMLAnchorElement>(null)
  const wasChoosingPlan = useRef(false)
  useEffect(() => {
    if (choosingPlan) firstChoiceRef.current?.focus()
    else if (wasChoosingPlan.current) planButtonRef.current?.focus()
    wasChoosingPlan.current = choosingPlan
  }, [choosingPlan])

  // Effects cannot themselves be async because their return value is reserved
  // for cleanup, so the request lives in an inner function and is launched below.
  useEffect(() => {
    let cancelled = false

    async function load() {
      setMessage(null)

      // Both lists at once. allSettled rather than all: each answer is used
      // on its own, so a failed plan list still shows the history.
      const [history, prepared] = await Promise.allSettled([
        listWorkouts(PAGE_SIZE),
        listPreparedWorkouts(),
      ])

      // React Strict Mode mounts effects twice in development, and navigation
      // may unmount this screen while fetch is pending. Ignore either stale
      // response rather than updating a component that is no longer current.
      if (cancelled) {
        return
      }

      if (history.status === 'fulfilled') {
        setWorkouts(history.value)
        setHasEarlierPages(history.value.length === PAGE_SIZE)
      } else {
        setMessage('The notebook could not be opened. Please try again.')
      }

      if (prepared.status === 'fulfilled') {
        setPlans(prepared.value)
        setPlansMessage(null)
      } else {
        setPlans(null)
        setPlansMessage('Your planned sessions could not be loaded.')
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

  // Reloads both lists in place, without the full-screen loading state: after
  // a Start that found its plan already started or gone, the lists on screen
  // are out of date, but there is no reason to blank them.
  function refresh() {
    setLoadAttempt((attempt) => attempt + 1)
  }

  // Start on a Planned row (specs/006): starts the session at once, with the
  // local date and this moment, and opens it in the editor, in progress. The
  // row has no revision to send, so none is: a plan changed elsewhere since
  // is still a plan, and starts as it now is.
  async function startPlan(plan: PreparedWorkoutSummaryResponse) {
    if (startingPlanId !== null) {
      return
    }

    setStartingPlanId(plan.id)
    setStartMessage(null)
    const { date, startedAt } = createStartTimes(new Date())

    try {
      await startWorkout(plan.id, date, startedAt)
      void navigate(`/workouts/${plan.id}/edit`)
    } catch (error: unknown) {
      setStartingPlanId(null)
      const name = plan.title ?? 'That plan'
      if (
        error instanceof ApiError &&
        error.status === 409 &&
        error.code === ALREADY_STARTED
      ) {
        // Started elsewhere meanwhile: it now belongs under Logged.
        setStartMessage(
          `${name} had already been started. It is under Logged now.`,
        )
        refresh()
      } else if (error instanceof ApiError && error.status === 404) {
        setStartMessage(`${name} no longer exists.`)
        refresh()
      } else {
        setStartMessage('The session could not be started. Please try again.')
      }
    }
  }

  function closeChooserOnEscape(event: KeyboardEvent<HTMLElement>) {
    if (event.key === 'Escape') {
      setChoosingPlan(false)
    }
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
          <Link className="header-link" to="/progress">
            Progress
          </Link>
          <Link className="header-link" to="/exercises">
            Exercises
          </Link>
          <Link className="header-link" to="/">
            Cover
          </Link>
        </nav>
      </header>
      {notice !== null && (
        <p className="form-message is-status route-notice" role="status">
          {notice}
        </p>
      )}

      <div className="sessions-list">
        {/* The Planned group (specs/006, docs/ui/README.md → Planned group).
            Not dated, so the history below stays one flat list (rule 1).
            PR 8 adds a Templates link to this label line. */}
        <section className="sessions-group" aria-labelledby="planned-title">
          <h2 id="planned-title" className="kicker sessions-group-label">
            Planned
          </h2>
          {startMessage !== null && (
            <p className="form-message sessions-start-message" role="alert">
              {startMessage}
            </p>
          )}
          {plansMessage !== null ? (
            <div className="sessions-plans-error">
              <p className="form-message" role="alert">
                {plansMessage}
              </p>
              <button className="btn btn-ghost" type="button" onClick={refresh}>
                Try again
              </button>
            </div>
          ) : plans === null ? (
            <p className="sessions-plans-empty">Opening plans…</p>
          ) : plans.length === 0 ? (
            <p className="sessions-plans-empty">
              Nothing planned. Use Plan a session to set one up ahead.
            </p>
          ) : (
            plans.map((plan) => {
              const title = plan.title ?? 'Untitled plan'
              const isStarting = startingPlanId === plan.id
              return (
                // The row opens the plan in the editor; Start sits beside
                // it, never inside it, as Continue logging does below.
                <div className="plan-entry" key={plan.id}>
                  <Link
                    className="session-row plan-row"
                    to={`/workouts/${plan.id}/edit`}
                  >
                    <span className="plan-marker">Plan</span>
                    <div className="session-content">
                      <h3
                        className={
                          plan.title === null ? 'is-untitled' : undefined
                        }
                      >
                        {title}
                      </h3>
                      <p className="session-exercises">
                        {plan.exerciseNames.length === 0
                          ? 'No exercises yet'
                          : plan.exerciseNames.join(' · ')}
                      </p>
                      <p className="session-meta num">
                        {formatCount(plan.exerciseCount, 'exercise')} ·{' '}
                        {formatCount(plan.setCount, 'set')}
                      </p>
                    </div>
                  </Link>
                  <button
                    className="btn btn-secondary plan-start"
                    type="button"
                    aria-label={`Start ${plan.title ?? 'untitled plan'}`}
                    disabled={startingPlanId !== null}
                    aria-busy={isStarting}
                    onClick={() => void startPlan(plan)}
                  >
                    {isStarting ? 'Starting…' : 'Start'}
                  </button>
                </div>
              )
            })
          )}
        </section>

        <h2 className="kicker sessions-group-label sessions-logged-label">
          Logged
        </h2>
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
            const inProgress = workout.endedAt === null
            // An in-progress row says so where a finished row shows its start
            // time, and its meta line carries the start once ("from 09.34")
            // instead of repeating it beside "–in progress".
            const timeRange =
              workout.endedAt === null
                ? `from ${startedAt}`
                : formatWorkoutTimeRange(workout.startedAt, workout.endedAt)

            const exerciseSummary =
              workout.exerciseNames.length === 0
                ? 'No exercises logged'
                : workout.exerciseNames.join(' · ')

            // The whole row is one link, giving mouse, touch, and keyboard
            // users a single generous target for opening the session page.
            const row = (
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
                    <h3>{workout.title ?? 'Untitled session'}</h3>
                    {inProgress ? (
                      <span className="session-time session-in-progress">
                        in progress
                      </span>
                    ) : (
                      <span className="session-time num">{startedAt}</span>
                    )}
                  </div>

                  <p className="session-exercises">{exerciseSummary}</p>

                  <p className="session-meta num">
                    {formatCount(workout.exerciseCount, 'exercise')} ·{' '}
                    {formatSetCounts(
                      workout.setCount,
                      workout.notDoneSetCount,
                      inProgress,
                    )}{' '}
                    · {timeRange}
                  </p>
                </div>
              </Link>
            )

            if (!inProgress) {
              return row
            }

            // A link can't hold another link, so "Continue logging" sits
            // beside the row rather than inside it: reading the page and
            // getting back to the rack stay two separate targets.
            return (
              <div className="session-entry" key={workout.id}>
                {row}
                <Link
                  className="btn btn-secondary session-continue"
                  to={`/workouts/${workout.id}/edit`}
                  aria-label={`Continue logging ${workout.title ?? 'untitled session'}, started ${startedAt}`}
                >
                  Continue logging
                </Link>
              </div>
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
        {choosingPlan && (
          <div
            className="plan-chooser"
            id="plan-chooser"
            role="group"
            aria-labelledby="plan-chooser-title"
            onKeyDown={closeChooserOnEscape}
          >
            <p id="plan-chooser-title" className="kicker">
              Plan a session from
            </p>
            <Link
              ref={firstChoiceRef}
              className="plan-chooser-choice"
              to="/workouts/prepare"
            >
              A blank page
            </Link>
            {/* PR 8 (T084): the templates by name, each with its exercise
                summary, then an "All templates" link beside Close, so a
                plan from a template is three taps from the cover (SC-007). */}
            <div className="plan-chooser-actions">
              <button type="button" onClick={() => setChoosingPlan(false)}>
                Close
              </button>
            </div>
          </div>
        )}
        <button
          ref={planButtonRef}
          className="btn btn-secondary"
          type="button"
          aria-expanded={choosingPlan}
          aria-controls={choosingPlan ? 'plan-chooser' : undefined}
          onClick={() => setChoosingPlan((open) => !open)}
        >
          Plan a session
        </button>
        <Link className="btn btn-primary" to="/workouts/new">
          Start a new page
        </Link>
      </footer>
    </main>
  )
}
