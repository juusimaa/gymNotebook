import { Fragment, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError } from '../api/client'
import {
  deleteWorkout,
  getWorkout,
  type SetEntryResponse,
  type WorkoutDetailResponse,
} from '../api/workouts'
import { saveWorkoutAsTemplate } from '../api/templates'
import {
  formatBestSet,
  formatPageHeadline,
  formatSetLoad,
  headlineExerciseNames,
  formatWorkoutLongDate,
  formatWorkoutTime,
  formatWorkoutTimeRange,
} from './workoutFormat'
import { routeNotice, useRouteNotice } from './routeNotice'
import { useOptionalDetailsAllowed } from './useOptionalDetailsAllowed'
import { SaveAsTemplateBand } from './SaveAsTemplateBand'
import './WorkoutDetail.css'

export default function WorkoutDetail() {
  const navigate = useNavigate()
  const { workoutId } = useParams()
  const parsedWorkoutId = Number(workoutId)
  const hasValidWorkoutId =
    Number.isSafeInteger(parsedWorkoutId) && parsedWorkoutId > 0
  const [workout, setWorkout] = useState<WorkoutDetailResponse | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [loadAttempt, setLoadAttempt] = useState(0)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [isDeleting, setIsDeleting] = useState(false)
  const [deleteMessage, setDeleteMessage] = useState<string | null>(null)
  // "Changes saved" from the editor that sent us here.
  const notice = useRouteNotice()
  // Opening the confirm puts focus on its safe answer, Keep; Keep hands it
  // back to Tear out. Without this, focus stayed on a button that had moved
  // out of the reading order, and a screen reader heard nothing new.
  const deleteButtonRef = useRef<HTMLButtonElement>(null)
  const keepButtonRef = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (confirmingDelete) {
      keepButtonRef.current?.focus()
    }
  }, [confirmingDelete])
  // Without optional-details consent (specs/001 user story 6) the title,
  // bodyweight, gym and notes are hidden here as in the editor. Such an
  // account has none stored anyway, apart from one still facing the
  // transition question, which the notebook gate stops before this screen.
  const detailsAllowed = useOptionalDetailsAllowed().status === 'allowed'
  // Save as template's band (specs/006). Closing it hands focus back to the
  // header button that opened it, as Keep does for Tear out.
  const [savingAsTemplate, setSavingAsTemplate] = useState(false)
  const saveAsTemplateButtonRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    let cancelled = false

    async function loadWorkout() {
      if (!hasValidWorkoutId) {
        setMessage('This session page does not exist.')
        return
      }

      setMessage(null)

      try {
        const response = await getWorkout(parsedWorkoutId)
        if (!cancelled) {
          setWorkout(response)
        }
      } catch (error: unknown) {
        if (!cancelled) {
          setMessage(
            error instanceof ApiError && error.status === 404
              ? 'This session page could not be found.'
              : 'This session page could not be opened. Please try again.',
          )
        }
      }
    }

    void loadWorkout()

    return () => {
      cancelled = true
    }
  }, [hasValidWorkoutId, loadAttempt, parsedWorkoutId])

  function retry() {
    setWorkout(null)
    setMessage(null)
    setLoadAttempt((attempt) => attempt + 1)
  }

  async function removeWorkout() {
    if (!hasValidWorkoutId || isDeleting) {
      return
    }

    setIsDeleting(true)
    setDeleteMessage(null)
    try {
      await deleteWorkout(parsedWorkoutId)
      void navigate('/workouts', { state: routeNotice('Page torn out.') })
    } catch {
      setDeleteMessage('This page could not be torn out. Please try again.')
      setIsDeleting(false)
    }
  }

  if (message !== null) {
    return (
      <main className="page workout-detail-state">
        <p className="form-message" role="alert">
          {message}
        </p>
        <div className="workout-detail-state-actions">
          {hasValidWorkoutId && (
            <button className="btn btn-secondary" type="button" onClick={retry}>
              Try again
            </button>
          )}
          <Link className="btn btn-ghost" to="/workouts">
            Back to sessions
          </Link>
        </div>
      </main>
    )
  }

  if (workout === null) {
    return <main className="page workout-detail-state">Opening page…</main>
  }

  // No end time means the lifter is still at it, so the page's main action is
  // getting back into the editor rather than reading.
  const inProgress = workout.endedAt === null
  const timeRange =
    workout.endedAt === null
      ? `from ${formatWorkoutTime(workout.startedAt)}, in progress`
      : formatWorkoutTimeRange(workout.startedAt, workout.endedAt)
  const bodyweight =
    workout.bodyweightKg === null
      ? 'bodyweight not logged'
      : `${workout.bodyweightKg.toFixed(1)} kg bodyweight`
  const location = workout.location ?? 'gym not logged'
  const meta = detailsAllowed
    ? `${timeRange} · ${bodyweight} · ${location}`
    : timeRange
  // The title is an optional detail; without it the page is headlined by its
  // exercises, which need no consent (formatPageHeadline).
  const shownTitle = detailsAllowed ? workout.title : null
  const hasTitle = shownTitle !== null && shownTitle.trim() !== ''
  const headlineNames = headlineExerciseNames(
    workout.exercises.map((exercise) => exercise.exerciseName),
  )

  return (
    <main className="page workout-detail">
      <header className="workout-detail-header">
        <Link className="header-link" to="/workouts">
          ← Sessions
        </Link>
        <nav className="workout-detail-actions" aria-label="Session actions">
          {/* While the session is in progress, "Continue logging" below is
              the way into the editor; one control per action, not two. */}
          {!inProgress && (
            <Link className="header-link" to={`/workouts/${workout.id}/edit`}>
              Edit
            </Link>
          )}
          {/* One band at a time: each opens under the header in the
              same place, so opening one closes the other. */}
          <button
            ref={saveAsTemplateButtonRef}
            className="header-link workout-detail-save-template"
            type="button"
            aria-expanded={savingAsTemplate}
            onClick={() => {
              setConfirmingDelete(false)
              setSavingAsTemplate(true)
            }}
          >
            Save as template
          </button>
          <button
            ref={deleteButtonRef}
            className="header-link"
            type="button"
            onClick={() => {
              setSavingAsTemplate(false)
              setConfirmingDelete(true)
            }}
          >
            Tear out
          </button>
        </nav>
      </header>

      {confirmingDelete && (
        <section className="workout-delete-confirmation" role="alert">
          <p>Tear out this page? {describeTearOut(workout.exercises)}</p>
          {deleteMessage !== null && (
            <p className="form-message">{deleteMessage}</p>
          )}
          <div>
            <button
              ref={keepButtonRef}
              className="btn btn-ghost"
              type="button"
              disabled={isDeleting}
              onClick={() => {
                setConfirmingDelete(false)
                deleteButtonRef.current?.focus()
              }}
            >
              Keep
            </button>
            <button
              className="btn btn-secondary"
              type="button"
              disabled={isDeleting}
              onClick={() => void removeWorkout()}
            >
              {/* The question keeps the notebook's word; the button that
                destroys says plainly what it does (design critique
                2026-10-16, second pass). */}
              {isDeleting ? 'Deleting…' : 'Delete page'}
            </button>
          </div>
        </section>
      )}

      {savingAsTemplate && (
        <SaveAsTemplateBand
          // The title is the name to start from, but only where the account
          // shows titles at all; otherwise the field starts empty.
          initialName={(detailsAllowed ? workout.title : null) ?? ''}
          onSave={async (name) => {
            // Copies the page as stored: every block and set, whatever each
            // set's status, so a skipped set is still part of the routine.
            await saveWorkoutAsTemplate(workout.id, name)
          }}
          onClose={() => {
            setSavingAsTemplate(false)
            saveAsTemplateButtonRef.current?.focus()
          }}
        />
      )}

      {notice !== null && (
        <p className="form-message is-status route-notice" role="status">
          {notice}
        </p>
      )}

      <div className="workout-detail-content">
        <section className="workout-detail-heading">
          <p className="workout-detail-date num">
            {formatWorkoutLongDate(workout.date)}
          </p>
          <h1>
            {hasTitle || headlineNames.length === 0
              ? formatPageHeadline(shownTitle, headlineNames)
              : // Untitled: the exercises, each name kept whole on its line
                // so "Pull-up" never breaks at its hyphen, and the "·" stays
                // at a line's end rather than starting the next.
                headlineNames.map((name, index) => (
                  <Fragment key={name}>
                    {index > 0 && ' '}
                    <span className="workout-detail-headline-name">
                      {name}
                      {index < headlineNames.length - 1 && ' ·'}
                    </span>
                  </Fragment>
                ))}
          </h1>
          <p className="workout-detail-meta num">{meta}</p>
        </section>

        <div className="accent-rule" aria-hidden="true"></div>

        <section className="workout-detail-exercises" aria-label="Exercises">
          {workout.exercises.length === 0 ? (
            <p className="muted workout-detail-empty">
              No exercises were logged on this page.
            </p>
          ) : (
            workout.exercises.map((exercise) => (
              <article className="workout-detail-exercise" key={exercise.id}>
                <div className="workout-detail-exercise-heading">
                  <div>
                    <h2>{exercise.exerciseName}</h2>
                    {exercise.isBodyweight && (
                      <span className="workout-detail-exercise-kind">
                        bodyweight
                      </span>
                    )}
                  </div>
                  <strong className="workout-detail-best num">
                    {formatBestSet(exercise.isBodyweight, exercise.sets)}
                  </strong>
                </div>

                <ol className="workout-detail-sets">
                  {exercise.sets.map((set) => (
                    <li
                      className={
                        set.isWarmup || set.status !== 'done'
                          ? 'is-demoted'
                          : undefined
                      }
                      key={set.id}
                    >
                      <span className="workout-detail-set-number num">
                        {set.setNumber}
                      </span>
                      <span className="workout-detail-set-load num">
                        {formatSetLoad(
                          exercise.isBodyweight,
                          set.weight,
                          set.reps,
                        )}
                      </span>
                      {/* Only exceptions are tagged: a done working set is
                          the default, and tagging every row made the tag
                          noise. A set not done (specs/006) is demoted like a
                          warm-up and never counts toward the best above:
                          "skipped" on a finished page, "to do" while the
                          session is still going. Its status outranks the
                          warm-up tag, since that is what decides whether it
                          happened. */}
                      {setTag(set) !== null && (
                        <span className="workout-detail-set-tag">
                          {setTag(set)}
                        </span>
                      )}
                    </li>
                  ))}
                </ol>
              </article>
            ))
          )}
        </section>

        {detailsAllowed && (
          <section
            className="workout-detail-notes"
            aria-labelledby="notes-title"
          >
            <h2 id="notes-title">Notes</h2>
            <p className={workout.notes === null ? 'muted' : undefined}>
              {workout.notes ?? 'No notes logged.'}
            </p>
          </section>
        )}
      </div>

      {inProgress && (
        <footer className="workout-detail-action">
          <Link
            className="btn btn-primary btn-block"
            to={`/workouts/${workout.id}/edit`}
          >
            Continue logging
          </Link>
        </footer>
      )}
    </main>
  )
}

// The one tag a set row carries, if any.
function setTag(set: SetEntryResponse): string | null {
  if (set.status === 'skipped') return 'skipped'
  if (set.status === 'planned') return 'to do'
  return set.isWarmup ? 'warm-up' : null
}

// What tearing out the page destroys, counted, since a destructive action
// names its consequence (PRODUCT.md, "Nothing is lost by accident").
function describeTearOut(exercises: { sets: unknown[] }[]): string {
  const setCount = exercises.reduce(
    (sum, exercise) => sum + exercise.sets.length,
    0,
  )
  if (exercises.length === 0) {
    return 'It has no exercises logged.'
  }
  const exerciseWord = exercises.length === 1 ? 'exercise' : 'exercises'
  const setWord = setCount === 1 ? 'set' : 'sets'
  return `Its ${exercises.length} ${exerciseWord} and ${setCount} ${setWord} go with it.`
}
