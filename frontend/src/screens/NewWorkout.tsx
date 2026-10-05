import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { flushSync } from 'react-dom'
import { Link, useNavigate, useParams, useRouteLoaderData } from 'react-router'
import type { MeResponse } from '../api/auth'
import { ApiError, isOptionalDetailsConsentRequired } from '../api/client'
import {
  searchExercises,
  updateExercise as updateExerciseRecord,
  type ExerciseResponse,
} from '../api/exercises'
import {
  createWorkout,
  getWorkout,
  replaceWorkoutExercises,
  updateWorkout,
} from '../api/workouts'
import {
  addSetToExercise,
  type DraftFieldRef,
  describeDraftSet,
  type ExistingWorkoutDraft,
  createExistingWorkoutDraft,
  createInitialHeadingDraft,
  createLocalEndedAt,
  createWorkoutExerciseDraft,
  describeHeadingWhen,
  dropOptionalDetails,
  prepareWorkoutDraft,
  removeSetFromExercise,
  restoreSetToExercise,
  updateSetInExercise,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
  type WorkoutSetDraft,
  type WorkoutSetDraftChanges,
} from './newWorkoutDraft'
import { getToken } from '../auth/token'
import { createClientId } from './clientId'
import {
  countDraftSets,
  describeDiscard,
  describeDraftSavedAt,
  draftFingerprint,
  loadEditorDraft,
  removeEditorDraft,
  saveEditorDraft,
  type EditorDraftRoute,
} from './editorDraftStorage'
import {
  describeLastSet,
  formatLastSet,
  normalizeExerciseName,
} from './exerciseFormat'
import { OptionalDetailsChoice } from './OptionalDetailsConsent'
import { useOptionalDetailsAllowed } from './useOptionalDetailsAllowed'
import './NewWorkout.css'

// The latest removal, kept so its "Removed … · Undo" line can put it back
// where it was. One at a time: a newer removal replaces it.
type Removal =
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

// A field a failed save points at, by the set's client id rather than its
// position, so the mark stays on the right row while rows come and go.
type InvalidField =
  | {
      kind: 'heading'
      field: 'date' | 'startTime' | 'bodyweightKg' | 'endTime'
    }
  | { kind: 'set'; setClientId: string; field: 'weight' | 'reps' }

// The ids that let focus move to a set's inputs right after a render.
function setFieldId(setClientId: string, field: 'weight' | 'reps'): string {
  return `set-${setClientId}-${field}`
}

const HEADING_FIELD_IDS: Record<
  Extract<InvalidField, { kind: 'heading' }>['field'],
  string
> = {
  date: 'workout-date',
  startTime: 'startTime',
  bodyweightKg: 'bodyweightKg',
  endTime: 'endTime',
}

const SAVE_MESSAGE_ID = 'new-workout-save-message'
const UNDO_BUTTON_ID = 'new-workout-undo'

// Moves focus to an input by id. `select` highlights its value, so typing
// replaces a copied figure instead of appending to it.
function focusInput(id: string, select = false) {
  const element = document.getElementById(id)
  if (!(element instanceof HTMLInputElement)) return
  element.focus()
  if (select) element.select()
}

// /workouts/new and /workouts/:id/edit render the same component. The key
// gives each route (and each workout) its own editor instance, so the state
// initialised from that route's stored draft never carries over to another.
export default function NewWorkout() {
  const { workoutId } = useParams()
  return <WorkoutEditor key={workoutId ?? 'new'} />
}

function WorkoutEditor() {
  const navigate = useNavigate()
  const user = useRouteLoaderData('auth') as MeResponse
  const { workoutId: workoutIdParam } = useParams()
  const parsedWorkoutId = Number(workoutIdParam)
  const isEditing = workoutIdParam !== undefined
  const hasValidWorkoutId =
    Number.isSafeInteger(parsedWorkoutId) && parsedWorkoutId > 0

  // Draft safety (editorDraftStorage.ts): the editor keeps a copy of its draft
  // in this tab's sessionStorage, so a reload, a locked phone or an expired
  // sign-in doesn't lose a half-logged session. The copy this route left is
  // read once, at mount, and wins over a fresh page or the server's copy.
  const draftRoute = useMemo<EditorDraftRoute | null>(() => {
    if (!isEditing) return { kind: 'new' }
    return hasValidWorkoutId
      ? { kind: 'edit', workoutId: parsedWorkoutId }
      : null
  }, [hasValidWorkoutId, isEditing, parsedWorkoutId])
  const [restoredDraft] = useState(() =>
    draftRoute === null ? null : loadEditorDraft(draftRoute, user.userId),
  )
  // Shown as "Restored … from 09.42" until the page is saved or discarded.
  const [restoredAt, setRestoredAt] = useState<string | null>(
    isEditing ? null : (restoredDraft?.savedAt ?? null),
  )
  // What the draft started from: a fresh page, or the server's copy once it
  // has loaded. Cancel asks before leaving only when the draft differs.
  const [baseline, setBaseline] = useState(() =>
    draftFingerprint({
      heading: createInitialHeadingDraft(new Date()),
      endTime: '',
      exercises: [],
    }),
  )
  const serverDraft = useRef<ExistingWorkoutDraft | null>(null)
  // Set once the draft is saved or discarded: the copy has been removed, and
  // the re-render before navigation must not write it back.
  const isDraftClosed = useRef(false)

  const [heading, setHeading] = useState(() =>
    !isEditing && restoredDraft !== null
      ? restoredDraft.heading
      : createInitialHeadingDraft(new Date()),
  )
  const [endTime, setEndTime] = useState('')
  const [exercises, setExercises] = useState<WorkoutExerciseDraft[]>(() =>
    !isEditing && restoredDraft !== null ? restoredDraft.exercises : [],
  )
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
  const [isLoading, setIsLoading] = useState(isEditing)
  const [loadMessage, setLoadMessage] = useState<string | null>(null)
  const [confirmingFinish, setConfirmingFinish] = useState(false)
  const [removal, setRemoval] = useState<Removal | null>(null)
  const [invalidField, setInvalidField] = useState<InvalidField | null>(null)
  // The page heading's fields start folded behind their one-line summary;
  // a save that fails on one of them opens it again.
  const [isHeadingOpen, setIsHeadingOpen] = useState(false)
  const pickerRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  // Cancel or "Start over" on a changed draft asks first, inline: 'leave'
  // goes back, 'reset' empties the editor and stays.
  const [confirmingDiscard, setConfirmingDiscard] = useState<
    'leave' | 'reset' | null
  >(null)
  const cancelRef = useRef<HTMLAnchorElement>(null)
  const keepEditingRef = useRef<HTMLButtonElement>(null)

  // Title, bodyweight, gym and notes need the account's consent (specs/001
  // user story 6). Without it their inputs are hidden and one entry offers to
  // enable them; the consent choice then opens right here, in their place, so
  // the draft survives it. Focus follows: to the title after "Allow", back to
  // the entry after "Not now".
  const optionalDetails = useOptionalDetailsAllowed()
  const detailsAllowed = optionalDetails.status === 'allowed'
  const [showingConsent, setShowingConsent] = useState(false)
  const entryRef = useRef<HTMLButtonElement>(null)
  const titleRef = useRef<HTMLInputElement>(null)
  const focusAfterConsent = useRef<'entry' | 'title' | null>(null)

  useEffect(() => {
    if (focusAfterConsent.current === 'entry') entryRef.current?.focus()
    if (focusAfterConsent.current === 'title') titleRef.current?.focus()
    focusAfterConsent.current = null
  }, [showingConsent, optionalDetails.status])

  // If the heading POST succeeds but a later request fails, retain its id. A
  // retry then updates that page instead of creating a duplicate empty page.
  // A restored new page keeps the id too, so a retry after a reload can't
  // create a duplicate either.
  const [savedWorkoutId, setSavedWorkoutId] = useState<number | null>(
    isEditing && hasValidWorkoutId
      ? parsedWorkoutId
      : (restoredDraft?.savedWorkoutId ?? null),
  )

  useEffect(() => {
    let cancelled = false

    async function loadWorkoutForEditing() {
      if (!isEditing) {
        setIsLoading(false)
        return
      }

      if (!hasValidWorkoutId) {
        setLoadMessage('This session page does not exist.')
        setIsLoading(false)
        return
      }

      try {
        const workout = await getWorkout(parsedWorkoutId)
        if (!cancelled) {
          const draft = createExistingWorkoutDraft(workout, createClientId)
          serverDraft.current = draft
          setBaseline(draftFingerprint(draft))
          // Unsaved changes from before a reload or sign-in win over the
          // server's copy; "Discard changes" goes back to it.
          const shown = restoredDraft ?? draft
          setHeading(shown.heading)
          setEndTime(shown.endTime)
          setExercises(shown.exercises)
          setRestoredAt(restoredDraft?.savedAt ?? null)
          setSavedWorkoutId(workout.id)
        }
      } catch (error: unknown) {
        if (!cancelled) {
          // The page is gone (deleted elsewhere, or never this user's), so a
          // stored copy of changes to it has nowhere to go. Any other failure
          // keeps the copy for the next try.
          if (
            error instanceof ApiError &&
            error.status === 404 &&
            draftRoute !== null
          ) {
            removeEditorDraft(draftRoute)
          }
          setLoadMessage(
            error instanceof ApiError && error.status === 404
              ? 'This session page could not be found.'
              : 'This session page could not be opened. Please try again.',
          )
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false)
        }
      }
    }

    void loadWorkoutForEditing()

    return () => {
      cancelled = true
    }
  }, [draftRoute, hasValidWorkoutId, isEditing, parsedWorkoutId, restoredDraft])

  const cancelTarget = isEditing ? `/workouts/${parsedWorkoutId}` : '/workouts'

  const isDirty =
    !isLoading && draftFingerprint({ heading, endTime, exercises }) !== baseline

  // Keeps the stored copy in step with the draft: written while it differs
  // from where it started, removed when it doesn't. Without consent the four
  // optional details are never stored (they're never shown or sent either).
  useEffect(() => {
    if (
      draftRoute === null ||
      isLoading ||
      loadMessage !== null ||
      isDraftClosed.current
    ) {
      return
    }
    // No token: the session has ended and invalidation has already cleared
    // or held the copy. Writing now would undo that.
    if (getToken() === null) {
      return
    }
    if (!isDirty) {
      removeEditorDraft(draftRoute)
      return
    }
    saveEditorDraft(draftRoute, {
      ownerId: user.userId,
      savedWorkoutId,
      heading:
        optionalDetails.status === 'not-allowed'
          ? dropOptionalDetails(heading)
          : heading,
      endTime,
      exercises,
    })
  }, [
    draftRoute,
    endTime,
    exercises,
    heading,
    isDirty,
    isLoading,
    loadMessage,
    optionalDetails.status,
    savedWorkoutId,
    user.userId,
  ])

  // The discard question takes focus so a keyboard user lands on the safe
  // answer; closing it without discarding returns focus to Cancel.
  useEffect(() => {
    if (confirmingDiscard !== null) keepEditingRef.current?.focus()
  }, [confirmingDiscard])

  function keepEditing() {
    const wasLeaving = confirmingDiscard === 'leave'
    setConfirmingDiscard(null)
    if (wasLeaving) cancelRef.current?.focus()
  }

  // Discarding removes the stored copy; then either leave, or start the
  // editor again from its baseline. A new page whose heading already reached
  // the server keeps that page (describeDiscard says so) but forgets its id,
  // so the fresh draft starts a page of its own.
  function discardDraft(action: 'leave' | 'reset') {
    if (draftRoute !== null) removeEditorDraft(draftRoute)
    setConfirmingDiscard(null)
    setRestoredAt(null)
    setSaveMessage(null)
    setInvalidField(null)
    setRemoval(null)
    setConfirmingFinish(false)

    if (action === 'leave') {
      isDraftClosed.current = true
      void navigate(cancelTarget)
      return
    }

    if (isEditing && serverDraft.current !== null) {
      setHeading(serverDraft.current.heading)
      setEndTime(serverDraft.current.endTime)
      setExercises(serverDraft.current.exercises)
    } else {
      const fresh = createInitialHeadingDraft(new Date())
      setBaseline(
        draftFingerprint({ heading: fresh, endTime: '', exercises: [] }),
      )
      setHeading(fresh)
      setEndTime('')
      setExercises([])
      setSavedWorkoutId(null)
    }
  }

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

  // The picker sits last in the scrolling column, right above the sticky
  // footer, so its answers (suggestions or the add-as choice) can open below
  // the visible area. While the search box has focus, bring them into view;
  // 'nearest' scrolls no more than it has to, and not at all when they fit.
  const pickerResultCount = exerciseSuggestions.length
  useEffect(() => {
    if (exerciseQuery.trim() === '' || isExerciseSearchLoading) return
    if (document.activeElement !== searchRef.current) return
    pickerRef.current?.scrollIntoView({ block: 'nearest' })
  }, [exerciseQuery, isExerciseSearchLoading, pickerResultCount])

  // Editing a field the last save complained about takes its mark off, and
  // the message with it: it no longer describes what's on screen.
  function updateHeadingField(field: keyof WorkoutHeadingDraft, value: string) {
    if (invalidField?.kind === 'heading' && invalidField.field === field) {
      setInvalidField(null)
      setSaveMessage(null)
    }
    setHeading((current) => ({ ...current, [field]: value }))
  }

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
    if (
      invalidField?.kind === 'set' &&
      invalidField.setClientId === setClientId
    ) {
      setInvalidField(null)
      setSaveMessage(null)
    }
    changeExercise(exerciseClientId, (exercise) =>
      updateSetInExercise(exercise, setClientId, changes),
    )
  }

  function isInvalid(field: InvalidField): boolean {
    if (invalidField === null || invalidField.kind !== field.kind) return false
    return invalidField.kind === 'set' && field.kind === 'set'
      ? invalidField.setClientId === field.setClientId &&
          invalidField.field === field.field
      : invalidField.field === field.field
  }

  // aria-invalid plus a pointer to the footer message, so a screen reader
  // hears why when it lands on the field.
  function invalidProps(field: InvalidField) {
    return isInvalid(field)
      ? { 'aria-invalid': true, 'aria-describedby': SAVE_MESSAGE_ID }
      : {}
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
        addSetToExercise(current, setClientId),
      ),
    )
    const hasWeight = !exercise.isBodyweight || exercise.isAddedWeightEnabled
    focusInput(setFieldId(setClientId, hasWeight ? 'weight' : 'reps'), true)
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

  // The Undo line, drawn where the removed item was. The button carries the
  // description, so focus landing on it says what Undo would bring back.
  function renderUndo(className: string) {
    if (removal === null) return null
    return (
      <p className={`new-workout-undo ${className}`}>
        <span id={`${UNDO_BUTTON_ID}-text`}>{removal.description}.</span>
        <button
          id={UNDO_BUTTON_ID}
          type="button"
          aria-describedby={`${UNDO_BUTTON_ID}-text`}
          onClick={undoRemoval}
        >
          Undo
        </button>
      </p>
    )
  }

  // A failed save marks the field it's about and moves focus there, opening
  // the folded heading first when the field is in it. Problems that aren't
  // one field's leave focus alone, except an empty page, whose fix is the
  // exercise search.
  function pointAtProblem(at: DraftFieldRef | InvalidField) {
    if ('area' in at && at.area === 'exercises') {
      setInvalidField(null)
      if (exercises.length === 0) searchRef.current?.focus()
      return
    }

    let field: InvalidField
    if ('kind' in at) {
      field = at
    } else if (at.area === 'heading') {
      field = { kind: 'heading', field: at.field }
    } else {
      const set = exercises[at.exerciseIndex]?.sets[at.setIndex]
      if (set === undefined) return
      field = { kind: 'set', setClientId: set.clientId, field: at.field }
    }

    flushSync(() => {
      setInvalidField(field)
      if (field.kind === 'heading') setIsHeadingOpen(true)
    })
    focusInput(
      field.kind === 'heading'
        ? HEADING_FIELD_IDS[field.field]
        : setFieldId(field.setClientId, field.field),
    )
  }

  // Repeated exercise selections remain separate blocks by design.
  function selectExercise(exercise: ExerciseResponse) {
    // "last time" only on a new page: on the edit page the server's latest
    // set may already be one of this page's own (newWorkoutDraft.ts).
    const draft = createWorkoutExerciseDraft(
      createClientId(),
      createClientId(),
      exercise.id,
      exercise.name,
      exercise.isBodyweight,
      isEditing ? null : exercise.lastSet,
    )

    addExerciseBlock(draft)
  }

  // A new block ends the Undo offer for a removed block, whose position
  // would otherwise be ambiguous.
  function addExerciseBlock(draft: WorkoutExerciseDraft) {
    if (removal?.kind === 'exercise') setRemoval(null)
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
      createClientId(),
      createClientId(),
      null,
      name,
      isBodyweight,
    )

    addExerciseBlock(draft)
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

    // Hidden values are never sent: without consent (or while it's still being
    // checked) the draft is saved without its optional details.
    const prepared = prepareWorkoutDraft(
      detailsAllowed ? heading : dropOptionalDetails(heading),
      exercises,
    )
    if (!prepared.ok) {
      setSaveMessage(prepared.message)
      pointAtProblem(prepared.at)
      return
    }

    // Capture the confirmation instant, not the later instant after network I/O.
    let endedAt: string | null = finishSession ? new Date().toISOString() : null
    if (isEditing && !finishSession && endTime !== '') {
      endedAt = createLocalEndedAt(
        prepared.value.workout.date,
        heading.startTime,
        endTime,
      )
      if (endedAt === null) {
        setSaveMessage('Enter a valid finish time.')
        pointAtProblem({ kind: 'heading', field: 'endTime' })
        return
      }
    }
    let workoutId = savedWorkoutId

    setSavingAction(finishSession ? 'finish' : 'save')
    setSaveMessage(null)
    setInvalidField(null)

    try {
      if (workoutId === null) {
        const created = await createWorkout(prepared.value.workout)
        workoutId = created.id
        setSavedWorkoutId(created.id)
      } else {
        // Corrections made after a failed attempt must reach the existing page.
        await updateWorkout(
          workoutId,
          isEditing
            ? { ...prepared.value.workout, endedAt }
            : prepared.value.workout,
        )
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

      if (!isEditing && endedAt !== null) {
        await updateWorkout(workoutId, { endedAt })
      }

      // Saved: the stored copy has done its job.
      isDraftClosed.current = true
      if (draftRoute !== null) removeEditorDraft(draftRoute)
      void navigate(isEditing ? `/workouts/${workoutId}` : '/workouts')
    } catch (error: unknown) {
      // Consent was withdrawn elsewhere while this page was open, and the
      // server refused the details (contracts/ui.md → Rejected save). Not a
      // session problem: keep the draft, drop only those details, say so, and
      // offer the opt-in entry again.
      if (isOptionalDetailsConsentRequired(error)) {
        setHeading(dropOptionalDetails)
        optionalDetails.setStatus('not-allowed')
        setSaveMessage(
          'This account no longer allows a title, bodyweight, gym or notes, so they were removed from this page and not saved. The rest of your draft is kept; save again.',
        )
        return
      }
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
  // Shown on the folded heading line only when the account allows it.
  const summaryTitle = detailsAllowed ? heading.title.trim() : ''

  // Still in progress: a new page, or an edit page without a finish time.
  // This is what offers "Finish session" and makes it the footer's primary.
  const canFinish = !isEditing || endTime === ''

  if (isLoading) {
    return <main className="page workout-editor-state">Opening page…</main>
  }

  if (loadMessage !== null) {
    return (
      <main className="page workout-editor-state">
        <p className="form-message" role="alert">
          {loadMessage}
        </p>
        <Link className="btn btn-ghost" to="/workouts">
          Back to sessions
        </Link>
      </main>
    )
  }

  return (
    <main className="page new-workout">
      <header className="new-workout-header">
        <Link
          className="header-link"
          ref={cancelRef}
          to={cancelTarget}
          onClick={(event) => {
            // An unchanged draft leaves silently; a changed one asks first.
            if (isDirty) {
              event.preventDefault()
              setConfirmingDiscard('leave')
            }
          }}
        >
          ← Cancel
        </Link>
        <h1>{isEditing ? 'Edit page' : 'New page'}</h1>
        <span className="new-workout-header-spacer" aria-hidden="true"></span>
      </header>

      {confirmingDiscard !== null && (
        <section className="new-workout-discard" role="alert">
          <p>
            {describeDiscard({
              isEditing,
              setCount: countDraftSets(exercises),
              partlySaved: !isEditing && savedWorkoutId !== null,
            })}
          </p>
          <div>
            <button
              ref={keepEditingRef}
              className="btn btn-ghost"
              type="button"
              onClick={keepEditing}
            >
              Keep editing
            </button>
            <button
              className="btn btn-secondary"
              type="button"
              onClick={() => discardDraft(confirmingDiscard)}
            >
              Discard
            </button>
          </div>
        </section>
      )}

      <form
        className="new-workout-form"
        onSubmit={(event) => event.preventDefault()}
      >
        <fieldset className="new-workout-fields" disabled={isSaving}>
          <div className="new-workout-content">
            {restoredAt !== null && (
              <p className="new-workout-restored" role="status">
                <span>
                  Restored your unsaved {isEditing ? 'changes' : 'page'} from{' '}
                  <span className="num">
                    {describeDraftSavedAt(restoredAt, new Date())}
                  </span>
                  .
                </span>
                <button
                  type="button"
                  onClick={() => setConfirmingDiscard('reset')}
                >
                  {isEditing ? 'Discard changes' : 'Start over'}
                </button>
              </p>
            )}
            {/* The page heading as one line. Date and start time already say
                "now", so the fields wait behind the line and the exercises
                come first (design-fix-plan step 2). */}
            <section
              className="new-workout-heading"
              aria-labelledby="heading-title"
            >
              <h2 id="heading-title" className="visually-hidden">
                Page heading
              </h2>
              <button
                className="new-workout-heading-summary"
                type="button"
                aria-expanded={isHeadingOpen}
                aria-controls="page-heading-fields"
                onClick={() => setIsHeadingOpen((open) => !open)}
              >
                <span className="new-workout-heading-line">
                  <span className="num">
                    {describeHeadingWhen(heading, endTime, new Date())}
                  </span>
                  {summaryTitle !== '' && <> · {summaryTitle}</>}
                </span>
                <span className="new-workout-heading-action">
                  {isHeadingOpen
                    ? 'hide'
                    : isEditing || summaryTitle !== ''
                      ? 'edit details'
                      : 'add details'}
                </span>
              </button>
              {isHeadingOpen && (
                <div className="form-stack" id="page-heading-fields">
                  <div className="new-workout-field-pair">
                    <div className="field">
                      <label className="label" htmlFor="workout-date">
                        Date
                      </label>
                      <input
                        className="input num"
                        id="workout-date"
                        type="date"
                        required
                        {...invalidProps({ kind: 'heading', field: 'date' })}
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
                        {...invalidProps({
                          kind: 'heading',
                          field: 'startTime',
                        })}
                        value={heading.startTime}
                        onChange={(event) =>
                          updateHeadingField('startTime', event.target.value)
                        }
                      />
                    </div>
                  </div>
                  {isEditing && (
                    <div className="field">
                      <label className="label" htmlFor="endTime">
                        Finished
                      </label>
                      <input
                        className="input num"
                        id="endTime"
                        type="time"
                        {...invalidProps({ kind: 'heading', field: 'endTime' })}
                        value={endTime}
                        onChange={(event) => {
                          if (
                            isInvalid({ kind: 'heading', field: 'endTime' })
                          ) {
                            setInvalidField(null)
                            setSaveMessage(null)
                          }
                          setEndTime(event.target.value)
                        }}
                      />
                      <span className="new-workout-field-hint">
                        Leave empty to mark the session in progress.
                      </span>
                    </div>
                  )}
                  {detailsAllowed && (
                    <>
                      <div className="field">
                        <label className="label" htmlFor="title">
                          Title
                        </label>
                        <input
                          className="input"
                          id="title"
                          ref={titleRef}
                          type="text"
                          value={heading.title}
                          onChange={(event) =>
                            updateHeadingField('title', event.target.value)
                          }
                        />
                      </div>
                      <div className="new-workout-field-pair">
                        <div className="field">
                          <label className="label" htmlFor="bodyweightKg">
                            Bodyweight (kg)
                          </label>
                          <input
                            className="input num"
                            id="bodyweightKg"
                            type="text"
                            inputMode="decimal"
                            {...invalidProps({
                              kind: 'heading',
                              field: 'bodyweightKg',
                            })}
                            value={heading.bodyweightKg}
                            onChange={(event) =>
                              updateHeadingField(
                                'bodyweightKg',
                                event.target.value,
                              )
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
                    </>
                  )}
                  {optionalDetails.status === 'not-allowed' &&
                    (showingConsent ? (
                      <OptionalDetailsChoice
                        declineLabel="Not now"
                        sectionHeadingLevel={3}
                        onAllowed={() => {
                          focusAfterConsent.current = 'title'
                          optionalDetails.setStatus('allowed')
                          setShowingConsent(false)
                        }}
                        onDeclined={() => {
                          focusAfterConsent.current = 'entry'
                          setShowingConsent(false)
                        }}
                      />
                    ) : (
                      <button
                        ref={entryRef}
                        className="btn btn-secondary btn-block"
                        type="button"
                        onClick={() => setShowingConsent(true)}
                      >
                        Add title, location, notes and bodyweight
                      </button>
                    ))}
                </div>
              )}
            </section>

            <section
              className="new-workout-exercises"
              aria-labelledby="exercise-picker-title"
            >
              <h2 id="exercise-picker-title" className="visually-hidden">
                Exercises
              </h2>

              {exercises.length === 0 && (
                <p className="muted new-workout-empty">
                  Search below to add the first exercise.
                </p>
              )}

              {exercises.map((exercise, exerciseIndex) => (
                <Fragment key={exercise.clientId}>
                  {removal?.kind === 'exercise' &&
                    removal.index === exerciseIndex &&
                    renderUndo('new-workout-undo-block')}
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
                              {formatLastSet(
                                exercise.lastSet,
                                exercise.isBodyweight,
                              )}
                            </span>
                          </p>
                        )}
                      </div>
                      <button
                        id={`remove-block-${exercise.clientId}`}
                        className="btn btn-ghost new-workout-remove-exercise"
                        type="button"
                        onClick={() => removeExercise(exercise)}
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
                      {/* One column header per block instead of a caption on
                          every row. Hidden from screen readers: each input
                          already names its exercise, set and column. */}
                      <div
                        className="new-workout-set-columns"
                        aria-hidden="true"
                      >
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
                      {exercise.sets.map((set, setIndex) => (
                        <Fragment key={set.clientId}>
                          {removal?.kind === 'set' &&
                            removal.exerciseClientId === exercise.clientId &&
                            removal.index === setIndex &&
                            renderUndo('new-workout-undo-set')}
                          <div className="new-workout-set">
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
                                aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, weight`}
                                {...invalidProps({
                                  kind: 'set',
                                  setClientId: set.clientId,
                                  field: 'weight',
                                })}
                                value={set.weight}
                                onChange={(event) =>
                                  changeSet(exercise.clientId, set.clientId, {
                                    weight: event.target.value,
                                  })
                                }
                              />
                            )}

                            <input
                              className="input num"
                              id={setFieldId(set.clientId, 'reps')}
                              type="text"
                              inputMode="numeric"
                              aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, reps`}
                              {...invalidProps({
                                kind: 'set',
                                setClientId: set.clientId,
                                field: 'reps',
                              })}
                              value={set.reps}
                              onChange={(event) =>
                                changeSet(exercise.clientId, set.clientId, {
                                  reps: event.target.value,
                                })
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
                                changeSet(exercise.clientId, set.clientId, {
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
                              onClick={() => removeSet(exercise, set)}
                            >
                              ×
                            </button>
                          </div>
                        </Fragment>
                      ))}
                      {removal?.kind === 'set' &&
                        removal.exerciseClientId === exercise.clientId &&
                        removal.index >= exercise.sets.length &&
                        renderUndo('new-workout-undo-set')}
                    </div>

                    <button
                      className="btn btn-ghost new-workout-add-set"
                      type="button"
                      onClick={() => addSet(exercise)}
                    >
                      + Add set
                    </button>
                  </article>
                </Fragment>
              ))}
              {removal?.kind === 'exercise' &&
                removal.index >= exercises.length &&
                renderUndo('new-workout-undo-block')}

              <div className="field exercise-picker" ref={pickerRef}>
                <label className="label" htmlFor="exercise-search">
                  Add exercise
                </label>
                <input
                  className="input"
                  id="exercise-search"
                  ref={searchRef}
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

                {/* Existing names come first and the add-as choice last, so a
                    near-miss like "Taka" offers "Takakyykky" before inviting a
                    typo'd duplicate. The wording says which case this is. */}
                {canCreateExercise && (
                  <div className="new-exercise-choice">
                    <p>
                      {exerciseSuggestions.length > 0 ? (
                        <>
                          Not in the list? Add{' '}
                          <strong>{exerciseQuery.trim()}</strong> as a new
                          exercise:
                        </>
                      ) : (
                        <>
                          No exercise called{' '}
                          <strong>{exerciseQuery.trim()}</strong> yet. Add it
                          as:
                        </>
                      )}
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
              </div>
            </section>
          </div>

          <footer className="new-workout-actions">
            {saveMessage !== null && (
              <p id={SAVE_MESSAGE_ID} className="form-message" role="alert">
                {saveMessage}
              </p>
            )}
            {confirmingFinish && (
              <div className="finish-confirmation" role="alert">
                <p>Finish this session now? The current time will be saved.</p>
                <div>
                  <button
                    className="btn btn-ghost"
                    type="button"
                    onClick={() => setConfirmingFinish(false)}
                  >
                    Keep editing
                  </button>
                  <button
                    className="btn btn-primary"
                    type="button"
                    onClick={() => void saveWorkout(true)}
                  >
                    Confirm finish
                  </button>
                </div>
              </div>
            )}
            <div className="new-workout-action-buttons">
              {/* One primary: Finish session while the session is still in
                  progress (a new page, or an edit reached by "Continue
                  logging"), Save changes once it has finished. */}
              <button
                className={canFinish ? 'btn btn-secondary' : 'btn btn-primary'}
                type="button"
                onClick={() => void saveWorkout(false)}
              >
                {savingAction === 'save'
                  ? 'Saving…'
                  : isEditing
                    ? 'Save changes'
                    : 'Save page'}
              </button>
              {canFinish && (
                <button
                  className="btn btn-primary"
                  type="button"
                  onClick={() => setConfirmingFinish(true)}
                >
                  {savingAction === 'finish' ? 'Finishing…' : 'Finish session'}
                </button>
              )}
            </div>
          </footer>
        </fieldset>
      </form>
    </main>
  )
}
