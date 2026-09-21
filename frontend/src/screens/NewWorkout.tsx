import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { searchExercises, type ExerciseResponse } from '../api/exercises'
import {
  createInitialHeadingDraft,
  createWorkoutExerciseDraft,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
} from './newWorkoutDraft'
import './NewWorkout.css'

// Format the autocomplete hint using the notebook's set notation. Bodyweight
// exercises prefix a non-null weight because it represents added load.
function describeLastSet(exercise: ExerciseResponse): string {
  const lastSet = exercise.lastSet

  if (lastSet === null) {
    return 'Not logged yet'
  }

  if (lastSet.weight === null) {
    return `${lastSet.reps} reps`
  }

  const prefix = exercise.isBodyweight ? '+' : ''
  return `${prefix}${lastSet.weight} kg × ${lastSet.reps}`
}

export default function NewWorkout() {
  // Lazy initialization captures the local date and time once when the screen mounts.
  const [heading, setHeading] = useState(() =>
    createInitialHeadingDraft(new Date()),
  )
  const [exercises, setExercises] = useState<WorkoutExerciseDraft[]>([])
  const [exerciseQuery, setExerciseQuery] = useState('')
  const [exerciseSuggestions, setExerciseSuggestions] = useState<
    ExerciseResponse[]
  >([])
  const [isExerciseSearchLoading, setIsExerciseSearchLoading] = useState(false)
  const [exerciseSearchMessage, setExerciseSearchMessage] = useState<
    string | null
  >(null)

  // Start a search for each query change. Cleanup marks the previous request as
  // stale so a slower response cannot replace results for a newer query.
  useEffect(() => {
    const query = exerciseQuery.trim()
    let cancelled = false

    async function loadSuggestions() {
      if (query === '') {
        setExerciseSuggestions([])
        setExerciseSearchMessage(null)
        setIsExerciseSearchLoading(false)
        return
      }

      setIsExerciseSearchLoading(true)
      setExerciseSearchMessage(null)

      try {
        const response = await searchExercises(query)

        if (!cancelled) {
          setExerciseSuggestions(response)
        }
      } catch {
        if (!cancelled) {
          setExerciseSuggestions([])
          setExerciseSearchMessage(
            'Exercises could not be loaded. Please try again.',
          )
        }
      } finally {
        if (!cancelled) {
          setIsExerciseSearchLoading(false)
        }
      }
    }

    void loadSuggestions()

    return () => {
      cancelled = true
    }
  }, [exerciseQuery])

  // `keyof` limits callers to fields that actually exist in WorkoutHeadingDraft.
  function updateHeadingField(field: keyof WorkoutHeadingDraft, value: string) {
    setHeading((current) => ({ ...current, [field]: value }))
  }

  // The exercise block and its initial set need independent, stable React keys.
  // Repeated exercise selections remain separate blocks by design.
  function selectExercise(exercise: ExerciseResponse) {
    const draft = createWorkoutExerciseDraft(
      crypto.randomUUID(),
      crypto.randomUUID(),
      exercise.id,
      exercise.name,
      exercise.isBodyweight,
    )

    setExercises((current) => [...current, draft])
    setExerciseQuery('')
    setExerciseSuggestions([])
    setExerciseSearchMessage(null)
  }

  return (
    <main className="page new-workout">
      <header className="new-workout-header">
        <Link to="/workouts">← Cancel</Link>
        <h1>New page</h1>
        <span className="new-workout-header-spacer" aria-hidden="true"></span>
      </header>
      <div className="new-workout-content">
        <section className="form-stack" aria-labelledby="heading-title">
          <h2 id="heading-title">Page heading</h2>
          <div className="field">
            <label className="label" htmlFor="workout-date">
              Date
            </label>
            <input
              className="input"
              id="workout-date"
              type="date"
              required
              value={heading.date}
              onChange={(event) =>
                updateHeadingField('date', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="startTime">
              Started
            </label>
            <input
              className="input"
              id="startTime"
              type="time"
              required
              value={heading.startTime}
              onChange={(event) =>
                updateHeadingField('startTime', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="title">
              Title
            </label>
            <input
              className="input"
              id="title"
              type="text"
              value={heading.title}
              onChange={(event) =>
                updateHeadingField('title', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="bodyweightKg">
              Bodyweight (kg)
            </label>
            <input
              className="input"
              id="bodyweightKg"
              type="text"
              inputMode="decimal"
              value={heading.bodyweightKg}
              onChange={(event) =>
                updateHeadingField('bodyweightKg', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="location">
              Gym
            </label>
            <input
              className="input"
              id="location"
              type="text"
              value={heading.location}
              onChange={(event) =>
                updateHeadingField('location', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="notes">
              Notes
            </label>
            <textarea
              className="input"
              id="notes"
              value={heading.notes}
              onChange={(event) =>
                updateHeadingField('notes', event.target.value)
              }
            />
          </div>
        </section>
        <section className="form-stack" aria-labelledby="exercise-picker-title">
          <h2 id="exercise-picker-title">Exercises</h2>
          {exercises.map((exercise) => (
            <article key={exercise.clientId}>
              <h3>{exercise.exerciseName}</h3>
              <span>{exercise.isBodyweight ? 'bodyweight' : 'kg'}</span>
            </article>
          ))}
          <div className="field">
            <label className="label" htmlFor="exercise-search">
              Add exercise
            </label>
            <input
              className="input"
              id="exercise-search"
              type="search"
              autoComplete="off"
              placeholder="Search exercises"
              value={exerciseQuery}
              onChange={(event) => setExerciseQuery(event.target.value)}
            />
            {isExerciseSearchLoading && (
              <p role="status">Searching exercises…</p>
            )}

            {exerciseSearchMessage !== null && (
              <p role="alert">{exerciseSearchMessage}</p>
            )}

            {/* Hide stale results while a newer search is loading or has failed. */}
            {exerciseQuery.trim() !== '' &&
              !isExerciseSearchLoading &&
              exerciseSearchMessage === null &&
              exerciseSuggestions.length === 0 && (
                <p role="status">No matching exercises.</p>
              )}

            {exerciseQuery.trim() !== '' &&
              !isExerciseSearchLoading &&
              exerciseSearchMessage === null &&
              exerciseSuggestions.length > 0 && (
                <ul aria-label="Exercise suggestions">
                  {exerciseSuggestions.map((exercise) => (
                    <li key={exercise.id}>
                      <button
                        type="button"
                        onClick={() => selectExercise(exercise)}
                      >
                        <span>{exercise.name}</span>
                        <span>
                          {exercise.isBodyweight ? 'bodyweight' : 'kg'}
                        </span>
                        <span>{describeLastSet(exercise)}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
          </div>
        </section>
      </div>
    </main>
  )
}
