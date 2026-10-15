import { useEffect, useRef, useState, type RefObject } from 'react'
import { searchExercises, type ExerciseResponse } from '../api/exercises'
import { describeLastSet, normalizeExerciseName } from './exerciseFormat'

// "Add exercise": the search under the blocks, its suggestions and the
// add-as choice for a name not in the index. Shared by the page editor and
// the template editor (specs/006 plan D7). Styled by NewWorkout.css.

// How long typing in the exercise search must pause before it asks the API.
// Short enough to feel immediate, long enough to skip most mid-word queries.
const EXERCISE_SEARCH_DELAY_MS = 250

interface ExercisePickerProps {
  // The search box, for an editor that focuses it (an empty page's save).
  inputRef: RefObject<HTMLInputElement | null>
  // The page being edited, left out of "last time" (GET /exercises).
  excludeWorkoutId?: number
  // An existing exercise was picked.
  onSelect: (exercise: ExerciseResponse) => void
  // A name not in the index, to become an exercise when the page or
  // template is saved, loaded (kg) or bodyweight.
  onSelectNew: (name: string, isBodyweight: boolean) => void
}

export function ExercisePicker({
  inputRef,
  excludeWorkoutId,
  onSelect,
  onSelectNew,
}: ExercisePickerProps) {
  const [exerciseQuery, setExerciseQuery] = useState('')
  const [exerciseSuggestions, setExerciseSuggestions] = useState<
    ExerciseResponse[]
  >([])
  // The query the shown suggestions answer, which lags the typed text while
  // a search is waiting or in flight.
  const [answeredExerciseQuery, setAnsweredExerciseQuery] = useState('')
  const [exerciseSearchMessage, setExerciseSearchMessage] = useState<
    string | null
  >(null)
  const pickerRef = useRef<HTMLDivElement>(null)

  // The search waits for typing to pause (debounce): each keystroke's cleanup
  // cancels the pending timer, so only the last query of a burst is sent. The
  // same cleanup marks an in-flight request as stale so a slower response
  // cannot replace results for a newer query.
  useEffect(() => {
    const query = exerciseQuery.trim()
    let cancelled = false

    async function loadSuggestions() {
      setExerciseSearchMessage(null)

      try {
        const response = await searchExercises(query, excludeWorkoutId)

        if (!cancelled) {
          setExerciseSuggestions(response)
          setAnsweredExerciseQuery(query)
        }
      } catch {
        if (!cancelled) {
          setExerciseSuggestions([])
          setAnsweredExerciseQuery(query)
          setExerciseSearchMessage(
            'Exercises could not be loaded. Please try again.',
          )
        }
      }
    }

    // An emptied box clears at once; there is nothing to wait for.
    const timer = window.setTimeout(
      () => {
        if (query === '') {
          setExerciseSuggestions([])
          setAnsweredExerciseQuery('')
          setExerciseSearchMessage(null)
        } else {
          void loadSuggestions()
        }
      },
      query === '' ? 0 : EXERCISE_SEARCH_DELAY_MS,
    )

    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [exerciseQuery, excludeWorkoutId])

  // Typed text the shown answer doesn't cover yet: the debounce is waiting or
  // the request is in flight.
  const isExerciseSearchPending = exerciseQuery.trim() !== answeredExerciseQuery

  // The picker sits last in the scrolling column, right above the sticky
  // footer, so its answers (suggestions or the add-as choice) can open below
  // the visible area. While the search box has focus, bring them into view;
  // 'nearest' scrolls no more than it has to, and not at all when they fit.
  // Keyed on the answer rather than the typed text, so it runs once per
  // search instead of on every keystroke.
  const pickerResultCount = exerciseSuggestions.length
  useEffect(() => {
    if (answeredExerciseQuery === '') return
    if (document.activeElement !== inputRef.current) return
    pickerRef.current?.scrollIntoView({ block: 'nearest' })
  }, [answeredExerciseQuery, pickerResultCount, inputRef])

  // Focusing the search box opens the keyboard, which shrinks the visible
  // area (see useVisualViewportHeight). Once it has settled, scroll the
  // picker back into view inside the editor's own scroller.
  function revealPicker() {
    window.setTimeout(() => {
      if (document.activeElement !== inputRef.current) return
      pickerRef.current?.scrollIntoView({ block: 'nearest' })
    }, 300)
  }

  function clearExercisePicker() {
    setExerciseQuery('')
    setExerciseSuggestions([])
    setAnsweredExerciseQuery('')
    setExerciseSearchMessage(null)
  }

  // The editor adds the block and moves focus into it first; the picker
  // empties itself after, in the same tap.
  function selectExercise(exercise: ExerciseResponse) {
    onSelect(exercise)
    clearExercisePicker()
  }

  // There is deliberately no standalone create-exercise endpoint. A name with
  // no exact match becomes an exercise when the page or template is saved.
  function selectNewExercise(isBodyweight: boolean) {
    const name = exerciseQuery.trim()
    if (name === '') return
    onSelectNew(name, isBodyweight)
    clearExercisePicker()
  }

  // The add-as choice appears once a search has answered and stays put while
  // the next one runs, so it doesn't blink on every keystroke. Its buttons
  // only work once the answer covers exactly what is typed: until then an
  // existing exercise with that name may not be in the list yet, and adding
  // would create a duplicate. It names the answered query, so it also stays
  // hidden while that one is an exact match — "Add Back squat" must not show
  // for a moment after typing past "Back squat".
  const isExactExerciseMatch = (name: string) =>
    exerciseSuggestions.some(
      (exercise) =>
        normalizeExerciseName(exercise.name) === normalizeExerciseName(name),
    )
  const showCreateExerciseChoice =
    exerciseQuery.trim() !== '' &&
    answeredExerciseQuery !== '' &&
    exerciseSearchMessage === null &&
    !isExactExerciseMatch(exerciseQuery) &&
    !isExactExerciseMatch(answeredExerciseQuery)
  const canCreateExercise = showCreateExerciseChoice && !isExerciseSearchPending

  return (
    <div className="field exercise-picker" ref={pickerRef}>
      <label className="label" htmlFor="exercise-search">
        Add exercise
      </label>
      <input
        className="input"
        id="exercise-search"
        ref={inputRef}
        type="search"
        autoComplete="off"
        placeholder="Search or enter a new exercise"
        value={exerciseQuery}
        onChange={(event) => setExerciseQuery(event.target.value)}
        onFocus={revealPicker}
      />

      {/* Only the first search after an empty box says so; later
          ones keep the previous answer on screen while they run. */}
      {isExerciseSearchPending && answeredExerciseQuery === '' && (
        <p className="muted exercise-search-status" role="status">
          Searching exercises…
        </p>
      )}

      {exerciseSearchMessage !== null && (
        <p className="form-message exercise-search-status" role="alert">
          {exerciseSearchMessage}
        </p>
      )}

      {exerciseQuery.trim() !== '' &&
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

      {/* Existing names come first and the add-as choice last, so a
          near-miss like "Taka" offers "Takakyykky" before inviting a
          typo'd duplicate. The wording says which case this is,
          for the query the list answers. */}
      {showCreateExerciseChoice && (
        <div className="new-exercise-choice">
          <p>
            {exerciseSuggestions.length > 0 ? (
              <>
                Not in the list? Add <strong>{answeredExerciseQuery}</strong> as
                a new exercise:
              </>
            ) : (
              <>
                No exercise called <strong>{answeredExerciseQuery}</strong> yet.
                Add it as:
              </>
            )}
          </p>
          <div>
            <button
              className="btn btn-secondary"
              type="button"
              disabled={!canCreateExercise}
              onClick={() => selectNewExercise(false)}
            >
              Loaded (kg)
            </button>
            <button
              className="btn btn-secondary"
              type="button"
              disabled={!canCreateExercise}
              onClick={() => selectNewExercise(true)}
            >
              Bodyweight
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
