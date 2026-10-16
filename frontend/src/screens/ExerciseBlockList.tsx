import {
  Fragment,
  useEffect,
  useState,
  type FocusEvent,
  type MouseEvent,
} from 'react'
import type { WorkoutExerciseDraft, WorkoutSetDraft } from './newWorkoutDraft'
import { formatLastSet } from './exerciseFormat'
import {
  setFieldId,
  setRowId,
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
  // The set the lifter is up to: its toggle carries the accent ring and its
  // number is set in ink.
  nextSetClientId: string | null
  // The word under a not-done row ("skipped"), or null for none.
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
  // The block whose "Remove" switch is on, if any. With a Done column, a row
  // has no × of its own and the block's "Remove exercise" is hidden: the
  // switch swaps each row's Warm-up for a × and "+ Add set" for "Remove
  // exercise", so neither the control tapped after every set nor the
  // heading sits one tap from a destructive action. Screen state only; it
  // isn't part of the draft.
  //
  // Design critique 2026-10-16, third pass: the × used to take Done's
  // place, and the switch stayed on until pressed again, so the next tap
  // where Done always is removed a set. Now Done never moves, and the
  // switch turns itself off after one removal, or once the lifter touches
  // or focuses anything outside the block. One block at a time.
  const [removingSetsFor, setRemovingSetsFor] = useState<string | null>(null)
  const toggleRemovingSets = (exerciseClientId: string) =>
    setRemovingSetsFor((current) =>
      current === exerciseClientId ? null : exerciseClientId,
    )

  // "Outside the block" is any pointer press or focus whose target sits in
  // another block, or in no block at all. Both, because iOS Safari doesn't
  // focus a tapped button, so focus alone would miss most taps on a phone.
  useEffect(() => {
    if (removingSetsFor === null) return
    const leaveIfOutside = (event: Event) => {
      const block =
        event.target instanceof Element
          ? event.target.closest('.new-workout-exercise')
          : null
      if (block?.getAttribute('data-client-id') !== removingSetsFor) {
        setRemovingSetsFor(null)
      }
    }
    document.addEventListener('pointerdown', leaveIfOutside)
    document.addEventListener('focusin', leaveIfOutside)
    return () => {
      document.removeEventListener('pointerdown', leaveIfOutside)
      document.removeEventListener('focusin', leaveIfOutside)
    }
  }, [removingSetsFor])

  const removingExercise =
    removingSetsFor === null
      ? undefined
      : exercises.find((exercise) => exercise.clientId === removingSetsFor)

  // Its id is where Undo puts focus back once the block returns
  // (useExerciseBlocks).
  const removeExerciseButton = (
    exercise: WorkoutExerciseDraft,
    className: string,
  ) => (
    <button
      id={`remove-block-${exercise.clientId}`}
      className={`${className} new-workout-remove-exercise`}
      type="button"
      aria-label={`Remove exercise ${exercise.exerciseName}`}
      onClick={() => {
        setRemovingSetsFor(null)
        blocks.removeExercise(exercise)
      }}
    >
      Remove exercise
    </button>
  )
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
      {/* The switch changes what every row offers, which its pressed state
        alone doesn't say. Polite: it follows the tap, never interrupts. */}
      {done !== undefined && (
        <p className="visually-hidden" role="status">
          {removingExercise === undefined
            ? ''
            : `Removing from ${removingExercise.exerciseName}: each set's Warm-up is now a remove button.`}
        </p>
      )}
      {exercises.map((exercise, exerciseIndex) => {
        // Without a Done column (a template) the × keeps its own track at
        // the row's end, as before.
        const isRemovingSets =
          done !== undefined && removingSetsFor === exercise.clientId
        const showsRemoveSet = done === undefined || isRemovingSets
        return (
          <Fragment key={exercise.clientId}>
            {removal?.kind === 'exercise' &&
              removal.index === exerciseIndex &&
              undo('new-workout-undo-block')}
            <article
              className="new-workout-exercise"
              data-client-id={exercise.clientId}
            >
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
                <div className="new-workout-exercise-actions">
                  {/* With a Done column: one switch, pressed or not, like
                    Warm-up, named for the exercise since every block has
                    one. A template has no Done column, so its rows keep
                    their × and its heading keeps Remove exercise. */}
                  {done !== undefined ? (
                    <button
                      className="new-workout-remove-sets"
                      type="button"
                      aria-pressed={isRemovingSets}
                      aria-label={`Remove from ${exercise.exerciseName}`}
                      onClick={() => toggleRemovingSets(exercise.clientId)}
                    >
                      Remove
                    </button>
                  ) : (
                    removeExerciseButton(exercise, 'btn btn-ghost')
                  )}
                </div>
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
                        id={setRowId(set.clientId)}
                        className={`new-workout-set${set.status === 'done' || done === undefined ? '' : ' is-not-done'}${isNext ? ' is-next' : ''}`}
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
                              blocks.changeSet(
                                exercise.clientId,
                                set.clientId,
                                {
                                  weight: event.target.value,
                                },
                              )
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
                          announce the opposite of its state. While the
                          block's Remove is on, the × takes its track. */}
                        {!isRemovingSets && (
                          <button
                            className="new-workout-set-kind"
                            type="button"
                            aria-pressed={set.isWarmup}
                            aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, warm-up`}
                            onClick={() =>
                              blocks.changeSet(
                                exercise.clientId,
                                set.clientId,
                                { isWarmup: !set.isWarmup },
                              )
                            }
                          >
                            Warm-up
                          </button>
                        )}

                        {/* One removal ends the mode: Undo takes focus,
                          and a second removal is a deliberate second
                          press of the switch. */}
                        {showsRemoveSet && (
                          <button
                            className="new-workout-remove-set"
                            type="button"
                            aria-label={`Remove ${exercise.exerciseName} set ${setIndex + 1}`}
                            disabled={exercise.sets.length === 1}
                            onClick={() => {
                              setRemovingSetsFor(null)
                              blocks.removeSet(exercise, set)
                            }}
                          >
                            ×
                          </button>
                        )}

                        {/* Done (specs/006): last, under the thumb, and
                          always there: the × appears in Warm-up's
                          place, never in Done's. A round
                          switch where Warm-up is a rectangular tag, and
                          not by colour alone: a filled disc with a check
                          when done, a hollow circle reading "Done" when
                          not. One name, pressed or not, like Warm-up,
                          and like it naming the exercise and set.
                          Released, a set goes back to planned, or to
                          skipped on a finished page (toggleSetDone).
                          Marking one done saves at once
                          (useWorkoutAutosave). Not on a template:
                          nothing in it can be done. */}
                        {done !== undefined && (
                          <button
                            className={`new-workout-set-done${isNext ? ' is-next' : ''}${set.status === 'skipped' ? ' is-skipped' : ''}`}
                            type="button"
                            aria-pressed={set.status === 'done'}
                            aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, done`}
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

              {/* While Remove is on, the block's next action is removing it,
                not adding to it. */}
              {isRemovingSets ? (
                removeExerciseButton(
                  exercise,
                  'btn btn-secondary new-workout-remove-block',
                )
              ) : (
                <button
                  className="btn btn-ghost new-workout-add-set"
                  type="button"
                  onClick={() => blocks.addSet(exercise)}
                >
                  + Add set
                </button>
              )}
            </article>
          </Fragment>
        )
      })}
      {removal?.kind === 'exercise' &&
        removal.index >= exercises.length &&
        undo('new-workout-undo-block')}
    </>
  )
}
