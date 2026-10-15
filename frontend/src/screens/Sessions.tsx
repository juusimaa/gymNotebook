import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { ApiError } from '../api/client'
import { listWorkouts, type WorkoutSummaryResponse } from '../api/workouts'
import {
  listTemplates,
  startFromTemplate,
  type TemplateSummaryResponse,
} from '../api/templates'
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

  // The templates the chooser offers (specs/006 SC-007: a session from a
  // template is three taps from the cover). Loaded with the history, so the
  // chooser opens complete; null with templatesFailed false is "loading".
  // A failure only costs the chooser its templates: All templates and a new
  // page still work.
  const [templates, setTemplates] = useState<TemplateSummaryResponse[] | null>(
    null,
  )
  const [templatesFailed, setTemplatesFailed] = useState(false)
  // The template a session is being started from, and what went wrong last
  // time.
  const [startingFromId, setStartingFromId] = useState<number | null>(null)
  const [chooserMessage, setChooserMessage] = useState<string | null>(null)
  const navigate = useNavigate()

  // "From template" opens a small chooser above the footer rather than a
  // modal. Opening it moves focus to its first template, or to All templates
  // when there is none; closing it (Close or Escape) hands focus back to the
  // button that opened it.
  const [choosingTemplate, setChoosingTemplate] = useState(false)
  const chooserButtonRef = useRef<HTMLButtonElement>(null)
  const firstTemplateRef = useRef<HTMLButtonElement>(null)
  const allTemplatesRef = useRef<HTMLAnchorElement>(null)
  const wasChoosingTemplate = useRef(false)
  useEffect(() => {
    if (choosingTemplate) {
      const first = firstTemplateRef.current ?? allTemplatesRef.current
      first?.focus()
    } else if (wasChoosingTemplate.current) {
      chooserButtonRef.current?.focus()
    }
    wasChoosingTemplate.current = choosingTemplate
  }, [choosingTemplate])

  // Effects cannot themselves be async because their return value is reserved
  // for cleanup, so the request lives in an inner function and is launched below.
  useEffect(() => {
    let cancelled = false

    async function load() {
      setMessage(null)

      // Both lists at once. allSettled rather than all: each answer is used
      // on its own, so a failed template list still shows the history.
      const [history, templateList] = await Promise.allSettled([
        listWorkouts(PAGE_SIZE),
        listTemplates(),
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

      if (templateList.status === 'fulfilled') {
        setTemplates(templateList.value)
        setTemplatesFailed(false)
      } else {
        setTemplates(null)
        setTemplatesFailed(true)
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
  // a template turned out to be gone, the chooser is out of date, but there
  // is no reason to blank the history.
  function refresh() {
    setLoadAttempt((attempt) => attempt + 1)
  }

  // A template picked in the chooser (specs/006): the server copies it into
  // a new session started now, with the local date and this moment, which
  // opens in the editor in progress, every set waiting to be ticked off.
  async function startSessionFrom(template: TemplateSummaryResponse) {
    if (startingFromId !== null) {
      return
    }

    setStartingFromId(template.id)
    setChooserMessage(null)
    const { date, startedAt } = createStartTimes(new Date())
    try {
      const session = await startFromTemplate(template.id, date, startedAt)
      void navigate(`/workouts/${session.id}/edit`)
    } catch (error: unknown) {
      setStartingFromId(null)
      if (error instanceof ApiError && error.status === 404) {
        // Deleted on another device: the chooser's list is out of date.
        setChooserMessage(`${template.name} no longer exists.`)
        refresh()
      } else {
        setChooserMessage('The session could not be started. Please try again.')
      }
    }
  }

  function closeChooserOnEscape(event: KeyboardEvent<HTMLElement>) {
    if (event.key === 'Escape') {
      setChoosingTemplate(false)
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
        {choosingTemplate && (
          <div
            className="template-chooser"
            id="template-chooser"
            role="group"
            aria-labelledby="template-chooser-title"
            onKeyDown={closeChooserOnEscape}
          >
            <p id="template-chooser-title" className="kicker">
              Start a session from
            </p>
            {/* The templates by name, so a session from one is three taps
                from the cover (SC-007): Open the notebook, From template,
                the template. Buttons, not links: picking one starts it. */}
            {templates?.map((template, index) => (
              <button
                key={template.id}
                ref={index === 0 ? firstTemplateRef : undefined}
                className="template-chooser-choice"
                type="button"
                disabled={startingFromId !== null}
                aria-busy={startingFromId === template.id}
                onClick={() => void startSessionFrom(template)}
              >
                <span className="template-chooser-name">
                  {startingFromId === template.id
                    ? `Starting ${template.name}…`
                    : template.name}
                </span>
                <span className="template-chooser-summary">
                  {template.exerciseNames.join(' · ')}
                </span>
              </button>
            ))}
            {templates?.length === 0 && (
              <p className="template-chooser-note">
                No templates yet. Write one under All templates, or save a
                session page as one.
              </p>
            )}
            {templatesFailed && (
              <p className="template-chooser-note">
                Your templates could not be loaded.
              </p>
            )}
            {chooserMessage !== null && (
              <p className="form-message template-chooser-note" role="alert">
                {chooserMessage}
              </p>
            )}
            <div className="template-chooser-actions">
              <Link
                ref={allTemplatesRef}
                className="header-link"
                to="/templates"
              >
                All templates
              </Link>
              <button type="button" onClick={() => setChoosingTemplate(false)}>
                Close
              </button>
            </div>
          </div>
        )}
        <button
          ref={chooserButtonRef}
          className="btn btn-secondary"
          type="button"
          aria-expanded={choosingTemplate}
          aria-controls={choosingTemplate ? 'template-chooser' : undefined}
          onClick={() => setChoosingTemplate((open) => !open)}
        >
          From template
        </button>
        <Link className="btn btn-primary" to="/workouts/new">
          Start a new page
        </Link>
      </footer>
    </main>
  )
}
