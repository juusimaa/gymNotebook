import {
  useState,
  type Dispatch,
  type KeyboardEvent,
  type SetStateAction,
} from 'react'
import { flushSync } from 'react-dom'
import type { SetStatus } from '../api/workouts'
import { createClientId } from './clientId'
import {
  addSetToExercise,
  describeDraftSet,
  removeSetFromExercise,
  restoreSetToExercise,
  updateSetInExercise,
  type WorkoutExerciseDraft,
  type WorkoutSetDraft,
  type WorkoutSetDraftChanges,
} from './newWorkoutDraft'

// Editing exercise blocks and their set rows, shared by the page editor
// (NewWorkout.tsx) and the template editor (EditTemplate.tsx, specs/006 plan
// D7). The blocks themselves stay in the caller's state, since the page
// editor also saves, stores and reloads them; this hook owns only what
// editing them needs: the latest removal for Undo, and focus after a change.

// The latest removal, kept so its "Removed … · Undo" line can put it back
// where it was. One at a time: a newer removal replaces it.
export type Removal =
  | {
      kind: 'set'
      exerciseClientId: string
      set: WorkoutSetDraft
      index: number
      description: string
    }
  | {
      kind: 'exercise'
      exercise: WorkoutExerciseDraft
      index: number
      description: string
    }

export const UNDO_BUTTON_ID = 'new-workout-undo'

// The ids that let focus move to a set's inputs right after a render.
export function setFieldId(
  setClientId: string,
  field: 'weight' | 'reps',
): string {
  return `set-${setClientId}-${field}`
}

// A set row's id, so the editor can bring the set the lifter is up to into
// view after a Done tap.
export function setRowId(setClientId: string): string {
  return `set-${setClientId}-row`
}

// Moves focus to an input by id. `select` highlights its value, so typing
// replaces a copied figure instead of appending to it.
export function focusInput(id: string, select = false) {
  const element = document.getElementById(id)
  if (!(element instanceof HTMLInputElement)) return
  element.focus()
  if (select) element.select()
}

export interface ExerciseBlocksOptions {
  exercises: WorkoutExerciseDraft[]
  setExercises: Dispatch<SetStateAction<WorkoutExerciseDraft[]>>
  // The status a new row starts with (newWorkoutDraft.ts → the factories).
  newRowStatus: SetStatus
  // Called before a set's figures change, so the caller can take a failed
  // save's mark off that set.
  onSetChange?: (setClientId: string) => void
}

export type ExerciseBlocks = ReturnType<typeof useExerciseBlocks>

export function useExerciseBlocks({
  exercises,
  setExercises,
  newRowStatus,
  onSetChange,
}: ExerciseBlocksOptions) {
  const [removal, setRemoval] = useState<Removal | null>(null)

  // Any edit to a block ends the Undo offer for a set removed from it: the
  // block has moved on, so putting the set back could land it somewhere
  // surprising (the agreed rule: the offer lasts until the next edit there).
  function changeExercise(
    clientId: string,
    change: (exercise: WorkoutExerciseDraft) => WorkoutExerciseDraft,
  ) {
    if (removal?.kind === 'set' && removal.exerciseClientId === clientId) {
      setRemoval(null)
    }
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
    onSetChange?.(setClientId)
    changeExercise(exerciseClientId, (exercise) =>
      updateSetInExercise(exercise, setClientId, changes),
    )
  }

  // "+ Add set" repeats the set above (addSetToExercise) and puts the cursor
  // in the new row with the copied figure selected: the same set again is
  // one tap, a heavier one is one tap and the new number. flushSync renders
  // the row first so it exists to focus, still inside the tap, which is what
  // lets a phone open its keyboard. A bodyweight block without added weight
  // has no weight field, so reps it is.
  function addSet(exercise: WorkoutExerciseDraft) {
    const setClientId = createClientId()
    flushSync(() =>
      changeExercise(exercise.clientId, (current) =>
        addSetToExercise(current, setClientId, newRowStatus),
      ),
    )
    focusSetEntry(exercise, setClientId)
  }

  // A set's first field: weight, or reps when the block has no weight field
  // (bodyweight without added weight). Selected, so a copied figure is typed
  // over rather than appended to.
  function focusSetEntry(exercise: WorkoutExerciseDraft, setClientId: string) {
    const hasWeight = !exercise.isBodyweight || exercise.isAddedWeightEnabled
    focusInput(setFieldId(setClientId, hasWeight ? 'weight' : 'reps'), true)
  }

  // Enter moves forward through a block, because a phone's numeric keypad
  // has no Tab key: weight → reps → the next set's first field. On the
  // block's last set it closes the keyboard ("done") rather than adding a
  // set: a ditto row added by a stray Enter after the final set would be
  // saved as a set nobody did. "+ Add set" stays the one way to add one.
  function advanceFromSetField(
    event: KeyboardEvent<HTMLInputElement>,
    exercise: WorkoutExerciseDraft,
    setIndex: number,
    field: 'weight' | 'reps',
  ) {
    // isComposing: Enter that confirms an IME composition isn't "next".
    if (event.key !== 'Enter' || event.nativeEvent.isComposing) return
    event.preventDefault()
    const set = exercise.sets[setIndex]
    if (field === 'weight') {
      focusInput(setFieldId(set.clientId, 'reps'), true)
      return
    }
    const next = exercise.sets[setIndex + 1]
    if (next === undefined) {
      event.currentTarget.blur()
    } else {
      focusSetEntry(exercise, next.clientId)
    }
  }

  // Removing never loses anything for good: the set or block goes, and an
  // Undo line takes its place and the focus, so a mis-tap at the rack is one
  // more tap to reverse.
  function removeSet(exercise: WorkoutExerciseDraft, set: WorkoutSetDraft) {
    const index = exercise.sets.findIndex(
      (candidate) => candidate.clientId === set.clientId,
    )
    const figures = describeDraftSet(exercise, set)
    flushSync(() => {
      changeExercise(exercise.clientId, (current) =>
        removeSetFromExercise(current, set.clientId),
      )
      setRemoval({
        kind: 'set',
        exerciseClientId: exercise.clientId,
        set,
        index,
        description: `Removed set ${index + 1}${figures === '' ? '' : ` · ${figures}`}`,
      })
    })
    document.getElementById(UNDO_BUTTON_ID)?.focus()
  }

  function removeExercise(exercise: WorkoutExerciseDraft) {
    const index = exercises.findIndex(
      (candidate) => candidate.clientId === exercise.clientId,
    )
    const setCount = exercise.sets.length
    flushSync(() => {
      setExercises((current) =>
        current.filter((candidate) => candidate.clientId !== exercise.clientId),
      )
      setRemoval({
        kind: 'exercise',
        exercise,
        index,
        description: `Removed ${exercise.exerciseName} and its ${setCount === 1 ? 'set' : `${setCount} sets`}`,
      })
    })
    document.getElementById(UNDO_BUTTON_ID)?.focus()
  }

  // Puts the removed set or block back at its old position (clamped, if the
  // list has since shrunk) and returns focus to what came back.
  function undoRemoval() {
    if (removal === null) return
    const restored = removal
    flushSync(() => {
      setRemoval(null)
      if (restored.kind === 'set') {
        setExercises((current) =>
          current.map((exercise) =>
            exercise.clientId === restored.exerciseClientId
              ? restoreSetToExercise(exercise, restored.set, restored.index)
              : exercise,
          ),
        )
      } else {
        setExercises((current) => {
          const next = [...current]
          next.splice(
            Math.min(restored.index, next.length),
            0,
            restored.exercise,
          )
          return next
        })
      }
    })
    if (restored.kind === 'set') {
      const weightId = setFieldId(restored.set.clientId, 'weight')
      focusInput(
        document.getElementById(weightId) === null
          ? setFieldId(restored.set.clientId, 'reps')
          : weightId,
      )
    } else {
      document
        .getElementById(`remove-block-${restored.exercise.clientId}`)
        ?.focus()
    }
  }

  // A new block ends the Undo offer for a removed block, whose position
  // would otherwise be ambiguous.
  // Focus goes straight to the new block's first set, still inside the tap
  // that picked it (flushSync renders the row first), so the keyboard opens
  // on the figure to log. With "last time" written in, that figure is
  // selected: the same set again is one tap away, a new one is typed over it.
  function addExerciseBlock(draft: WorkoutExerciseDraft) {
    if (removal?.kind === 'exercise') setRemoval(null)
    flushSync(() => setExercises((current) => [...current, draft]))
    focusSetEntry(draft, draft.sets[0].clientId)
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

  return {
    removal,
    setRemoval,
    changeSet,
    addSet,
    advanceFromSetField,
    removeSet,
    removeExercise,
    undoRemoval,
    addExerciseBlock,
    setAddedWeightEnabled,
  }
}
