import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError } from '../api/client'
import {
  searchExercises,
  updateExercise,
  type ExerciseResponse,
} from '../api/exercises'
import { describeLastSet, normalizeExerciseName } from './exerciseFormat'
import { formatCount } from './workoutFormat'
import './ExerciseManagement.css'

export default function EditExercise() {
  const navigate = useNavigate()
  const { exerciseId } = useParams()
  const parsedExerciseId = Number(exerciseId)
  const hasValidExerciseId =
    Number.isSafeInteger(parsedExerciseId) && parsedExerciseId > 0
  const [exercise, setExercise] = useState<ExerciseResponse | null>(null)
  const [allExercises, setAllExercises] = useState<ExerciseResponse[]>([])
  const [name, setName] = useState('')
  const [isBodyweight, setIsBodyweight] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [isSaving, setIsSaving] = useState(false)

  useEffect(() => {
    let cancelled = false

    async function loadExercise() {
      if (!hasValidExerciseId) {
        setMessage('This exercise does not exist.')
        return
      }

      try {
        const response = await searchExercises('')
        const selected = response.find(
          (candidate) => candidate.id === parsedExerciseId,
        )
        if (!cancelled) {
          if (selected === undefined) {
            setMessage('This exercise could not be found.')
            return
          }
          setAllExercises(response)
          setExercise(selected)
          setName(selected.name)
          setIsBodyweight(selected.isBodyweight)
        }
      } catch {
        if (!cancelled) {
          setMessage('This exercise could not be opened. Please try again.')
        }
      }
    }

    void loadExercise()
    return () => {
      cancelled = true
    }
  }, [hasValidExerciseId, parsedExerciseId])

  const normalizedName = normalizeExerciseName(name)
  const collision = allExercises.find(
    (candidate) =>
      candidate.id !== parsedExerciseId &&
      normalizeExerciseName(candidate.name) === normalizedName,
  )

  async function saveExercise() {
    if (exercise === null || isSaving) {
      return
    }

    if (name.trim() === '') {
      setMessage('Enter an exercise name.')
      return
    }

    setIsSaving(true)
    setMessage(null)
    try {
      await updateExercise(exercise.id, {
        name: name.trim(),
        isBodyweight,
      })
      void navigate('/exercises')
    } catch (error: unknown) {
      setMessage(
        error instanceof ApiError && error.status === 404
          ? 'This exercise could not be found.'
          : 'This exercise could not be saved. Please try again.',
      )
      setIsSaving(false)
    }
  }

  if (exercise === null) {
    return (
      <main className="page exercise-management-state">
        {message === null ? (
          'Opening exercise…'
        ) : (
          <p className="form-message" role="alert">
            {message}
          </p>
        )}
        <Link className="btn btn-ghost" to="/exercises">
          Back to exercises
        </Link>
      </main>
    )
  }

  return (
    <main className="page edit-exercise">
      <header className="edit-exercise-header">
        <Link to="/exercises">← Exercises</Link>
        <h1>Edit exercise</h1>
        <span aria-hidden="true"></span>
      </header>

      <form
        className="edit-exercise-content"
        onSubmit={(event) => {
          event.preventDefault()
          void saveExercise()
        }}
      >
        <fieldset disabled={isSaving}>
          <p className="edit-exercise-label">As logged</p>
          <h2>{exercise.name}</h2>
          <p className="edit-exercise-meta num">
            {formatCount(exercise.sessionCount, 'session')} · last{' '}
            {describeLastSet(exercise).toLowerCase()}
          </p>

          <div className="hr"></div>

          <label className="field">
            <span className="label">Name</span>
            <input
              className="input"
              type="text"
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
          </label>
          <p className="edit-exercise-hint">
            Casing and spacing are yours to keep — “Back Squat” and “back squat”
            are the same exercise underneath.
          </p>

          {collision !== undefined && (
            <aside className="exercise-merge-notice">
              <strong>This name already exists</strong>
              <p>
                Saving merges this exercise into <b>{collision.name}</b>. Its{' '}
                {formatCount(exercise.sessionCount, 'session')} move across and
                “{exercise.name}” disappears from the index. Nothing is deleted,
                and it cannot be undone from here.
              </p>
            </aside>
          )}

          <div className="hr"></div>

          <div className="edit-exercise-load">
            <div>
              <p className="edit-exercise-label">Load</p>
              <strong>{isBodyweight ? 'Bodyweight' : 'Loaded (kg)'}</strong>
            </div>
            <button
              className="btn btn-ghost"
              type="button"
              onClick={() => setIsBodyweight((current) => !current)}
            >
              Switch
            </button>
          </div>
          <p className="edit-exercise-hint">
            {isBodyweight
              ? 'Sets chart by reps. Weight, when entered, is added weight only.'
              : 'Sets chart by estimated 1RM from weight × reps.'}
          </p>

          <div className="hr"></div>

          <p className="edit-exercise-label">Remove</p>
          <p className="edit-exercise-hint">
            An exercise with sets against it can’t be deleted — that would take
            training history with it. Rename it onto the correct name instead;
            that merges the two.
          </p>

          {message !== null && (
            <p className="form-message" role="alert">
              {message}
            </p>
          )}
          <div className="edit-exercise-actions">
            <Link className="btn btn-ghost" to="/exercises">
              Cancel
            </Link>
            <button className="btn btn-primary" type="submit">
              {isSaving
                ? 'Saving…'
                : collision === undefined
                  ? 'Save name'
                  : `Merge into ${collision.name}`}
            </button>
          </div>
        </fieldset>
      </form>
    </main>
  )
}
