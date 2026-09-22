import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError } from '../api/client'
import {
  deleteWorkout,
  getWorkout,
  type WorkoutDetailResponse,
} from '../api/workouts'
import {
  formatBestSet,
  formatSetLoad,
  formatWorkoutLongDate,
  formatWorkoutTime,
} from './workoutFormat'
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
      void navigate('/workouts')
    } catch {
      setDeleteMessage(
        'This session page could not be deleted. Please try again.',
      )
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

  const startedAt = formatWorkoutTime(workout.startedAt)
  const timeRange =
    workout.endedAt === null
      ? `${startedAt}–in progress`
      : `${startedAt}–${formatWorkoutTime(workout.endedAt)}`
  const bodyweight =
    workout.bodyweightKg === null
      ? 'bodyweight not logged'
      : `${workout.bodyweightKg.toFixed(1)} kg bodyweight`
  const location = workout.location ?? 'gym not logged'

  return (
    <main className="page workout-detail">
      <header className="workout-detail-header">
        <Link to="/workouts">← Sessions</Link>
        <nav className="workout-detail-actions" aria-label="Session actions">
          <Link to={`/workouts/${workout.id}/edit`}>Edit</Link>
          <button type="button" onClick={() => setConfirmingDelete(true)}>
            Delete
          </button>
        </nav>
      </header>

      {confirmingDelete && (
        <section className="workout-delete-confirmation" role="alert">
          <p>Tear out this page? Its exercises and sets go with it.</p>
          {deleteMessage !== null && (
            <p className="form-message">{deleteMessage}</p>
          )}
          <div>
            <button
              className="btn btn-ghost"
              type="button"
              disabled={isDeleting}
              onClick={() => setConfirmingDelete(false)}
            >
              Keep
            </button>
            <button
              className="btn btn-secondary"
              type="button"
              disabled={isDeleting}
              onClick={() => void removeWorkout()}
            >
              {isDeleting ? 'Deleting…' : 'Delete page'}
            </button>
          </div>
        </section>
      )}

      <div className="workout-detail-content">
        <section className="workout-detail-heading">
          <p className="workout-detail-date num">
            {formatWorkoutLongDate(workout.date)}
          </p>
          <h1>{workout.title ?? 'Untitled session'}</h1>
          <p className="workout-detail-meta num">
            {timeRange} · {bodyweight} · {location}
          </p>
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
                      className={set.isWarmup ? 'is-warmup' : undefined}
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
                      <span className="workout-detail-set-tag">
                        {set.isWarmup ? 'warm-up' : 'working'}
                      </span>
                    </li>
                  ))}
                </ol>
              </article>
            ))
          )}
        </section>

        <section className="workout-detail-notes" aria-labelledby="notes-title">
          <h2 id="notes-title">Notes</h2>
          <p className={workout.notes === null ? 'muted' : undefined}>
            {workout.notes ?? 'No notes logged.'}
          </p>
        </section>
      </div>
    </main>
  )
}
