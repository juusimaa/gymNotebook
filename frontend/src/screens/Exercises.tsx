import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { searchExercises, type ExerciseResponse } from '../api/exercises'
import { describeLastSet } from './exerciseFormat'
import { formatCount } from './workoutFormat'
import './ExerciseManagement.css'

export default function Exercises() {
  const [query, setQuery] = useState('')
  const [exercises, setExercises] = useState<ExerciseResponse[] | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    async function loadExercises() {
      setMessage(null)
      try {
        const response = await searchExercises(query.trim())
        if (!cancelled) {
          setExercises(response)
        }
      } catch {
        if (!cancelled) {
          setExercises([])
          setMessage('Exercises could not be loaded. Please try again.')
        }
      }
    }

    void loadExercises()
    return () => {
      cancelled = true
    }
  }, [query])

  return (
    <main className="page exercise-index">
      <header className="exercise-management-header">
        <div>
          <p className="kicker">Index</p>
          <h1>Exercises</h1>
        </div>
        <nav aria-label="Exercise navigation">
          <Link to="/workouts">Sessions</Link>
          <Link to="/progress">Progress</Link>
        </nav>
      </header>

      <div className="exercise-index-content">
        <input
          className="input"
          type="search"
          autoComplete="off"
          aria-label="Find an exercise"
          placeholder="Find an exercise"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
        />
        <p className="exercise-index-explanation">
          Exercises come into being by being used. Tap one to fix a name or mark
          it bodyweight.
        </p>

        {message !== null && (
          <p className="form-message" role="alert">
            {message}
          </p>
        )}
        {exercises === null && <p className="muted">Opening the index…</p>}
        {exercises !== null && exercises.length === 0 && message === null && (
          <p className="muted exercise-index-empty">
            {query.trim() === ''
              ? 'No exercises have been logged yet.'
              : 'No exercises match that search.'}
          </p>
        )}
        {exercises !== null && exercises.length > 0 && (
          <ul className="exercise-index-list">
            {exercises.map((exercise) => (
              <li key={exercise.id}>
                <Link to={`/exercises/${exercise.id}`}>
                  <span>
                    <strong>{exercise.name}</strong>
                    <small>
                      {formatCount(exercise.sessionCount, 'session')} · last{' '}
                      {describeLastSet(exercise)}
                    </small>
                  </span>
                  <span className="exercise-index-kind">
                    {exercise.isBodyweight ? 'bodyweight' : 'kg'}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>
    </main>
  )
}
