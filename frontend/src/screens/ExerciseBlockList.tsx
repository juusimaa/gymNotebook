import { Fragment, type FocusEvent, type MouseEvent } from 'react'
import type { WorkoutExerciseDraft, WorkoutSetDraft } from './newWorkoutDraft'
import { formatLastSet } from './exerciseFormat'
import {
  setFieldId,
  UNDO_BUTTON_ID,
  type ExerciseBlocks,
  type Removal,
} from './useExerciseBlocks'

// The exercise blocks and their set rows, shared by the page editor and the
// template editor (specs/006 plan D7). Styled by NewWorkout.css, which both
// editors load.

// A tapped weight or reps field selects its figure too, the same as one the
// editor focuses itself: the copied "10" is replaced by typing "15", with no
// select-all or backspacing on a phone. A second tap places the caret as
// usual, for editing one digit.
//
// Two browser quirks need handling. The tap's own mouseup, which arrives
// after focus, clears the selection in Chrome and Safari, so the first
// mouseup after focus is cancelled. And iOS ignores select() on inputs, and
// can still move the caret after the focus event, so the range is set
// through setSelectionRange both at once and again on the next frame.
const selectedOnFocus = new WeakSet<HTMLInputElement>()

const selectOnFocusProps = {
  onFocus(event: FocusEvent<HTMLInputElement>) {
    const input = event.currentTarget
    const selectAll = () => {
      if (document.activeElement === input) {
        input.setSelectionRange(0, input.value.length)
      }
    }
    selectAll()
    requestAnimationFrame(selectAll)
    selectedOnFocus.add(input)
  },
  onMouseUp(event: MouseEvent<HTMLInputElement>) {
    if (selectedOnFocus.delete(event.currentTarget)) event.preventDefault()
  },
  onBlur(event: FocusEvent<HTMLInputElement>) {
    selectedOnFocus.delete(event.currentTarget)
  },
}

// The Done column (specs/006), on a started page only: a plan and a template
// have nothing done yet, so their rows keep the plain layout.
export interface DoneColumn {
  // The set the lifter is up to, marked "next".
  nextSetClientId: string | null
  // The word under a not-done row ("next", "skipped"), or null for none.
  note: (set: WorkoutSetDraft) => string | null
  onToggle: (exercise: WorkoutExerciseDraft, set: WorkoutSetDraft) => void
}

// The attributes a set field gets when a failed save points at it.
export interface InvalidAttributes {
  'aria-invalid'?: boolean
  'aria-describedby'?: string
}

interface ExerciseBlockListProps {
  exercises: WorkoutExerciseDraft[]
  blocks: ExerciseBlocks
  invalidProps: (
    setClientId: string,
    field: 'weight' | 'reps',
  ) => InvalidAttributes
  done?: DoneColumn
}

// The Undo line, drawn where the removed item was. The button carries the
// description, so focus landing on it says what Undo would bring back.
function UndoLine({
  removal,
  onUndo,
  className,
}: {
  removal: Removal
  onUndo: () => void
  className: string
}) {
  return (
    <p className={`new-workout-undo ${className}`}>
      <span id={`${UNDO_BUTTON_ID}-text`}>{removal.description}.</span>
      <button
        id={UNDO_BUTTON_ID}
        type="button"
        aria-describedby={`${UNDO_BUTTON_ID}-text`}
        onClick={onUndo}
      >
        Undo
      </button>
    </p>
  )
}

export function ExerciseBlockList({
  exercises,
  blocks,
  invalidProps,
  done,
}: ExerciseBlockListProps) {
  const { removal } = blocks
  const undo = (className: string) =>
    removal === null ? null : (
      <UndoLine
        removal={removal}
        onUndo={blocks.undoRemoval}
        className={className}
      />
    )

  return (
    <>
      {exercises.map((exercise, exerciseIndex) => (
        <Fragment key={exercise.clientId}>
          {removal?.kind === 'exercise' &&
            removal.index === exerciseIndex &&
            undo('new-workout-undo-block')}
          <article className="new-workout-exercise">
            <div className="new-workout-exercise-heading">
              <div>
                <h3>{exercise.exerciseName}</h3>
                <span className="new-workout-exercise-kind">
                  {exercise.isBodyweight ? 'bodyweight' : 'kg'}
                </span>
                {/* The paper log's habit of glancing at last week's
                    line before loading the bar. */}
                {exercise.lastSet != null && (
                  <p className="new-workout-last">
                    last time{' '}
                    <span className="num">
                      {formatLastSet(exercise.lastSet, exercise.isBodyweight)}
                    </span>
                  </p>
                )}
              </div>
              <button
                id={`remove-block-${exercise.clientId}`}
                className="btn btn-ghost new-workout-remove-exercise"
                type="button"
                onClick={() => blocks.removeExercise(exercise)}
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
                  blocks.setAddedWeightEnabled(
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

            {/* has-done adds the Done column. A plan has none: its
                sets are all still to do, so its rows keep the plain
                layout and solid fields (docs/ui/README.md → Plan). */}
            <div
              className={`new-workout-sets${done === undefined ? '' : ' has-done'}`}
            >
              {/* One column header per block instead of a caption on
                  every row. Hidden from screen readers: each input
                  already names its exercise, set and column. */}
              <div className="new-workout-set-columns" aria-hidden="true">
                <span></span>
                <span>
                  {!exercise.isBodyweight
                    ? 'Weight'
                    : exercise.isAddedWeightEnabled
                      ? 'Added kg'
                      : 'Load'}
                </span>
                <span>Reps</span>
              </div>
              {exercise.sets.map((set, setIndex) => {
                const note = done?.note(set) ?? null
                const isNext = done?.nextSetClientId === set.clientId
                return (
                  <Fragment key={set.clientId}>
                    {removal?.kind === 'set' &&
                      removal.exerciseClientId === exercise.clientId &&
                      removal.index === setIndex &&
                      undo('new-workout-undo-set')}
                    <div
                      className={`new-workout-set${set.status === 'done' || done === undefined ? '' : ' is-not-done'}`}
                    >
                      <span className="new-workout-set-number num">
                        {setIndex + 1}
                      </span>

                      {exercise.isBodyweight &&
                      !exercise.isAddedWeightEnabled ? (
                        <span className="new-workout-bodyweight-load">
                          Bodyweight
                        </span>
                      ) : (
                        <input
                          className="input num"
                          id={setFieldId(set.clientId, 'weight')}
                          type="text"
                          inputMode="decimal"
                          enterKeyHint="next"
                          aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, weight`}
                          {...invalidProps(set.clientId, 'weight')}
                          {...selectOnFocusProps}
                          value={set.weight}
                          onChange={(event) =>
                            blocks.changeSet(exercise.clientId, set.clientId, {
                              weight: event.target.value,
                            })
                          }
                          onKeyDown={(event) =>
                            blocks.advanceFromSetField(
                              event,
                              exercise,
                              setIndex,
                              'weight',
                            )
                          }
                        />
                      )}

                      <input
                        className="input num"
                        id={setFieldId(set.clientId, 'reps')}
                        type="text"
                        inputMode="numeric"
                        enterKeyHint={
                          setIndex === exercise.sets.length - 1
                            ? 'done'
                            : 'next'
                        }
                        aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, reps`}
                        {...invalidProps(set.clientId, 'reps')}
                        {...selectOnFocusProps}
                        value={set.reps}
                        onChange={(event) =>
                          blocks.changeSet(exercise.clientId, set.clientId, {
                            reps: event.target.value,
                          })
                        }
                        onKeyDown={(event) =>
                          blocks.advanceFromSetField(
                            event,
                            exercise,
                            setIndex,
                            'reps',
                          )
                        }
                      />

                      {/* A switch with one name: "Warm-up", pressed or
                          not. A label that flipped between "Working"
                          and "Warm-up" would make a screen reader
                          announce the opposite of its state. */}
                      <button
                        className="new-workout-set-kind"
                        type="button"
                        aria-pressed={set.isWarmup}
                        onClick={() =>
                          blocks.changeSet(exercise.clientId, set.clientId, {
                            isWarmup: !set.isWarmup,
                          })
                        }
                      >
                        Warm-up
                      </button>

                      <button
                        className="new-workout-remove-set"
                        type="button"
                        aria-label={`Remove ${exercise.exerciseName} set ${setIndex + 1}`}
                        disabled={exercise.sets.length === 1}
                        onClick={() => blocks.removeSet(exercise, set)}
                      >
                        ×
                      </button>

                      {/* Done (specs/006): last, under the thumb, so the
                          destructive × is never the end of the row. A
                          round switch where Warm-up is a rectangular
                          tag, and not by colour alone: a filled disc
                          with a check when done, a hollow circle
                          reading "Done" when not. One name, pressed or
                          not, like Warm-up. Released, a set goes back
                          to planned, or to skipped on a finished page
                          (toggleSetDone). Marking one done saves at
                          once (useWorkoutAutosave). Not on a plan:
                          nothing can be done before the session
                          starts. */}
                      {done !== undefined && (
                        <button
                          className={`new-workout-set-done${isNext ? ' is-next' : ''}${set.status === 'skipped' ? ' is-skipped' : ''}`}
                          type="button"
                          aria-pressed={set.status === 'done'}
                          aria-label="Done"
                          aria-describedby={
                            note === null ? undefined : `${set.clientId}-note`
                          }
                          onClick={() => done.onToggle(exercise, set)}
                        >
                          {set.status === 'done' ? (
                            <svg
                              width="20"
                              height="20"
                              viewBox="0 0 20 20"
                              fill="none"
                              stroke="currentColor"
                              strokeWidth="2.2"
                              strokeLinecap="round"
                              strokeLinejoin="round"
                              aria-hidden="true"
                            >
                              <path d="M4 10.5l4 4 8-9" />
                            </svg>
                          ) : (
                            'Done'
                          )}
                        </button>
                      )}
                    </div>
                    {note !== null && (
                      <p
                        id={`${set.clientId}-note`}
                        className="new-workout-set-note"
                      >
                        {note}
                      </p>
                    )}
                  </Fragment>
                )
              })}
              {removal?.kind === 'set' &&
                removal.exerciseClientId === exercise.clientId &&
                removal.index >= exercise.sets.length &&
                undo('new-workout-undo-set')}
            </div>

            <button
              className="btn btn-ghost new-workout-add-set"
              type="button"
              onClick={() => blocks.addSet(exercise)}
            >
              + Add set
            </button>
          </article>
        </Fragment>
      ))}
      {removal?.kind === 'exercise' &&
        removal.index >= exercises.length &&
        undo('new-workout-undo-block')}
    </>
  )
}
