import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  searchExercises,
  updateExercise as updateExerciseRecord,
  type ExerciseResponse,
} from '../api/exercises'
import {
  createWorkout,
  replaceWorkoutExercises,
  updateWorkout,
} from '../api/workouts'
import {
  addEmptySetToExercise,
  createInitialHeadingDraft,
  createWorkoutExerciseDraft,
  prepareWorkoutDraft,
  removeSetFromExercise,
  updateSetInExercise,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
  type WorkoutSetDraftChanges,
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

function normalizeExerciseName(name: string): string {
  return name.trim().toLowerCase().replace(/\s+/g, ' ')
}

export default function NewWorkout() {
  const navigate = useNavigate()
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
  const [savingAction, setSavingAction] = useState<'save' | 'finish' | null>(
    null,
  )
  const [saveMessage, setSaveMessage] = useState<string | null>(null)

  // If the heading POST succeeds but a later request fails, retain its id. A
  // retry then updates that page instead of creating a duplicate empty page.
  const [savedWorkoutId, setSavedWorkoutId] = useState<number | null>(null)

  // Cleanup marks the previous request as stale so a slower response cannot
  // replace results for a newer query.
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

  function updateHeadingField(field: keyof WorkoutHeadingDraft, value: string) {
    setHeading((current) => ({ ...current, [field]: value }))
  }

  function changeExercise(
    clientId: string,
    change: (exercise: WorkoutExerciseDraft) => WorkoutExerciseDraft,
  ) {
    setExercises((current) =>
      current.map((exercise) =>
        exercise.clientId === clientId ? change(exercise) : exercise,
      ),
    )
  }

  function changeSet(
    exerciseClientId: string,
    setClientId: string,
    changes: WorkoutSetDraftChanges,
  ) {
    changeExercise(exerciseClientId, (exercise) =>
      updateSetInExercise(exercise, setClientId, changes),
    )
  }

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
    clearExercisePicker()
  }

  // There is deliberately no standalone create-exercise endpoint. A name with
  // no exact match becomes an exercise when the workout bulk write is saved.
  function selectNewExercise(isBodyweight: boolean) {
    const name = exerciseQuery.trim()
    if (name === '') {
      return
    }

    const draft = createWorkoutExerciseDraft(
      crypto.randomUUID(),
      crypto.randomUUID(),
      null,
      name,
      isBodyweight,
    )

    setExercises((current) => [...current, draft])
    clearExercisePicker()
  }

  function clearExercisePicker() {
    setExerciseQuery('')
    setExerciseSuggestions([])
    setExerciseSearchMessage(null)
  }

  function setAddedWeightEnabled(clientId: string, enabled: boolean) {
    changeExercise(clientId, (exercise) => ({
      ...exercise,
      isAddedWeightEnabled: enabled,
      // Turning the field off also clears hidden values so they cannot be saved.
      sets: enabled
        ? exercise.sets
        : exercise.sets.map((set) => ({ ...set, weight: '' })),
    }))
  }

  async function saveWorkout(finishSession: boolean) {
    const isSaving = savingAction !== null
    if (isSaving) {
      return
    }

    const prepared = prepareWorkoutDraft(heading, exercises)
    if (!prepared.ok) {
      setSaveMessage(prepared.message)
      return
    }

    // Capture the button-click instant, not the later instant after network I/O.
    const endedAt = finishSession ? new Date().toISOString() : null
    let workoutId = savedWorkoutId

    setSavingAction(finishSession ? 'finish' : 'save')
    setSaveMessage(null)

    try {
      if (workoutId === null) {
        const created = await createWorkout(prepared.value.workout)
        workoutId = created.id
        setSavedWorkoutId(created.id)
      } else {
        // Corrections made after a failed attempt must reach the existing page.
        await updateWorkout(workoutId, prepared.value.workout)
      }

      const saved = await replaceWorkoutExercises(
        workoutId,
        prepared.value.exercises,
      )

      // New exercises start as loaded on the backend. The bulk response now
      // supplies their ids, allowing an ask-on-create bodyweight choice to stick.
      const newBodyweightExerciseIds = new Set<number>()
      exercises.forEach((exercise, index) => {
        if (exercise.exerciseId === null && exercise.isBodyweight) {
          const savedExercise = saved.exercises[index]
          if (savedExercise !== undefined) {
            newBodyweightExerciseIds.add(savedExercise.exerciseId)
          }
        }
      })

      await Promise.all(
        [...newBodyweightExerciseIds].map((exerciseId) =>
          updateExerciseRecord(exerciseId, { isBodyweight: true }),
        ),
      )

      if (endedAt !== null) {
        await updateWorkout(workoutId, { endedAt })
      }

      void navigate('/workouts')
    } catch {
      setSaveMessage(
        workoutId === null
          ? 'The page could not be saved. Please try again.'
          : 'The page was started but not fully saved. Your draft is still here; try again.',
      )
    } finally {
      setSavingAction(null)
    }
  }

  const canCreateExercise =
    exerciseQuery.trim() !== '' &&
    !isExerciseSearchLoading &&
    exerciseSearchMessage === null &&
    !exerciseSuggestions.some(
      (exercise) =>
        normalizeExerciseName(exercise.name) ===
        normalizeExerciseName(exerciseQuery),
    )
  const isSaving = savingAction !== null

  return (
    <main className="page new-workout">
      <header className="new-workout-header">
        <Link to="/workouts">← Cancel</Link>
        <h1>New page</h1>
        <span className="new-workout-header-spacer" aria-hidden="true"></span>
      </header>

      <form
        className="new-workout-form"
        onSubmit={(event) => event.preventDefault()}
      >
        <fieldset className="new-workout-fields" disabled={isSaving}>
          <div className="new-workout-content">
            <section className="form-stack" aria-labelledby="heading-title">
              <h2 id="heading-title">Page heading</h2>
              <div className="field">
                <label className="label" htmlFor="workout-date">
                  Date
                </label>
                <input
                  className="input num"
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
                  className="input num"
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
                  className="input num"
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

            <section
              className="form-stack new-workout-exercises"
              aria-labelledby="exercise-picker-title"
            >
              <h2 id="exercise-picker-title">Exercises</h2>

              {exercises.length === 0 && (
                <p className="muted new-workout-empty">
                  Search below to add the first exercise.
                </p>
              )}

              {exercises.map((exercise) => (
                <article
                  className="new-workout-exercise"
                  key={exercise.clientId}
                >
                  <div className="new-workout-exercise-heading">
                    <div>
                      <h3>{exercise.exerciseName}</h3>
                      <span className="new-workout-exercise-kind">
                        {exercise.isBodyweight ? 'bodyweight' : 'kg'}
                      </span>
                    </div>
                    <button
                      className="btn btn-ghost new-workout-remove-exercise"
                      type="button"
                      onClick={() =>
                        setExercises((current) =>
                          current.filter(
                            (candidate) =>
                              candidate.clientId !== exercise.clientId,
                          ),
                        )
                      }
                    >
                      Remove
                    </button>
                  </div>

                  {exercise.isBodyweight && (
                    <button
                      className="btn btn-secondary new-workout-added-weight"
                      type="button"
                      aria-pressed={exercise.isAddedWeightEnabled}
                      onClick={() =>
                        setAddedWeightEnabled(
                          exercise.clientId,
                          !exercise.isAddedWeightEnabled,
                        )
                      }
                    >
                      {exercise.isAddedWeightEnabled
                        ? 'Use bodyweight only'
                        : 'Add extra weight'}
                    </button>
                  )}

                  <div className="new-workout-sets">
                    {exercise.sets.map((set, setIndex) => (
                      <div className="new-workout-set" key={set.clientId}>
                        <span className="new-workout-set-number num">
                          {setIndex + 1}
                        </span>

                        {exercise.isBodyweight &&
                        !exercise.isAddedWeightEnabled ? (
                          <div className="new-workout-bodyweight-load">
                            <span>Load</span>
                            <strong>Bodyweight</strong>
                          </div>
                        ) : (
                          <label className="new-workout-set-field">
                            <span>
                              {exercise.isBodyweight ? 'Added kg' : 'Weight'}
                            </span>
                            <input
                              className="input num"
                              type="text"
                              inputMode="decimal"
                              aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, weight`}
                              value={set.weight}
                              onChange={(event) =>
                                changeSet(exercise.clientId, set.clientId, {
                                  weight: event.target.value,
                                })
                              }
                            />
                          </label>
                        )}

                        <label className="new-workout-set-field">
                          <span>Reps</span>
                          <input
                            className="input num"
                            type="text"
                            inputMode="numeric"
                            aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, reps`}
                            value={set.reps}
                            onChange={(event) =>
                              changeSet(exercise.clientId, set.clientId, {
                                reps: event.target.value,
                              })
                            }
                          />
                        </label>

                        <button
                          className={`new-workout-set-kind ${set.isWarmup ? 'is-warmup' : ''}`}
                          type="button"
                          aria-pressed={set.isWarmup}
                          onClick={() =>
                            changeSet(exercise.clientId, set.clientId, {
                              isWarmup: !set.isWarmup,
                            })
                          }
                        >
                          {set.isWarmup ? 'Warm-up' : 'Working'}
                        </button>

                        <button
                          className="new-workout-remove-set"
                          type="button"
                          aria-label={`Remove ${exercise.exerciseName} set ${setIndex + 1}`}
                          disabled={exercise.sets.length === 1}
                          onClick={() =>
                            changeExercise(exercise.clientId, (current) =>
                              removeSetFromExercise(current, set.clientId),
                            )
                          }
                        >
                          ×
                        </button>
                      </div>
                    ))}
                  </div>

                  <button
                    className="btn btn-ghost new-workout-add-set"
                    type="button"
                    onClick={() =>
                      changeExercise(exercise.clientId, (current) =>
                        addEmptySetToExercise(current, crypto.randomUUID()),
                      )
                    }
                  >
                    + Add set
                  </button>
                </article>
              ))}

              <div className="field exercise-picker">
                <label className="label" htmlFor="exercise-search">
                  Add exercise
                </label>
                <input
                  className="input"
                  id="exercise-search"
                  type="search"
                  autoComplete="off"
                  placeholder="Search or enter a new exercise"
                  value={exerciseQuery}
                  onChange={(event) => setExerciseQuery(event.target.value)}
                />

                {isExerciseSearchLoading && (
                  <p className="muted exercise-search-status" role="status">
                    Searching exercises…
                  </p>
                )}

                {exerciseSearchMessage !== null && (
                  <p
                    className="form-message exercise-search-status"
                    role="alert"
                  >
                    {exerciseSearchMessage}
                  </p>
                )}

                {canCreateExercise && (
                  <div className="new-exercise-choice">
                    <p>
                      No exact match. Add{' '}
                      <strong>{exerciseQuery.trim()}</strong> as:
                    </p>
                    <div>
                      <button
                        className="btn btn-secondary"
                        type="button"
                        onClick={() => selectNewExercise(false)}
                      >
                        Loaded (kg)
                      </button>
                      <button
                        className="btn btn-secondary"
                        type="button"
                        onClick={() => selectNewExercise(true)}
                      >
                        Bodyweight
                      </button>
                    </div>
                  </div>
                )}

                {exerciseQuery.trim() !== '' &&
                  !isExerciseSearchLoading &&
                  exerciseSearchMessage === null &&
                  exerciseSuggestions.length > 0 && (
                    <ul
                      className="exercise-suggestions"
                      aria-label="Exercise suggestions"
                    >
                      {exerciseSuggestions.map((exercise) => (
                        <li key={exercise.id}>
                          <button
                            className="exercise-suggestion"
                            type="button"
                            onClick={() => selectExercise(exercise)}
                          >
                            <span className="exercise-suggestion-name">
                              {exercise.name}
                            </span>
                            <span className="exercise-suggestion-kind">
                              {exercise.isBodyweight ? 'bodyweight' : 'kg'}
                            </span>
                            <span className="exercise-suggestion-last">
                              {describeLastSet(exercise)}
                            </span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
              </div>
            </section>
          </div>

          <footer className="new-workout-actions">
            {saveMessage !== null && (
              <p className="form-message" role="alert">
                {saveMessage}
              </p>
            )}
            <div>
              <button
                className="btn btn-secondary"
                type="button"
                onClick={() => void saveWorkout(false)}
              >
                {savingAction === 'save' ? 'Saving…' : 'Save page'}
              </button>
              <button
                className="btn btn-primary"
                type="button"
                onClick={() => void saveWorkout(true)}
              >
                {savingAction === 'finish' ? 'Finishing…' : 'Finish session'}
              </button>
            </div>
          </footer>
        </fieldset>
      </form>
    </main>
  )
}
