import {
  Fragment,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type FocusEvent,
  type KeyboardEvent,
  type MouseEvent,
} from 'react'
import { flushSync } from 'react-dom'
import {
  Link,
  useLocation,
  useNavigate,
  useParams,
  useRouteLoaderData,
} from 'react-router'
import type { MeResponse } from '../api/auth'
import { ApiError, isOptionalDetailsConsentRequired } from '../api/client'
import {
  searchExercises,
  updateExercise as updateExerciseRecord,
  type ExerciseResponse,
} from '../api/exercises'
import {
  createWorkout,
  deleteWorkout,
  getWorkout,
  PAGE_CHANGED,
  replaceWorkoutExercises,
  updateWorkout,
  type CreateWorkoutRequest,
  type UpdateWorkoutRequest,
} from '../api/workouts'
import {
  addSetToExercise,
  type DraftFieldRef,
  describeDraftSet,
  describeUnsavedSets,
  type ExistingWorkoutDraft,
  createExistingWorkoutDraft,
  createInitialHeadingDraft,
  createLocalEndedAt,
  createWorkoutExerciseDraft,
  describeHeadingWhen,
  dropOptionalDetails,
  needsExplicitFinishTime,
  prepareAutosaveDraft,
  prepareWorkoutDraft,
  type PreparedWorkoutDraft,
  removeSetFromExercise,
  restoreSetToExercise,
  updateSetInExercise,
  withLastTime,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
  type WorkoutSetDraft,
  type WorkoutSetDraftChanges,
} from './newWorkoutDraft'
import { getToken } from '../auth/token'
import { createClientId } from './clientId'
import { routeNotice } from './routeNotice'
import {
  countDraftSets,
  describeDiscard,
  describeDraftSavedAt,
  describeTearOut,
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
import {
  autosaveTrigger,
  useWorkoutAutosave,
  withExerciseIds,
} from './useWorkoutAutosave'
import { useOptionalDetailsAllowed } from './useOptionalDetailsAllowed'
import { useVisualViewportHeight } from './useVisualViewportHeight'
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
  | { kind: 'finishTime' }

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
const FINISH_BUTTON_ID = 'new-workout-finish'
const FINISH_TIME_ID = 'new-workout-finish-time'
// How long typing in the exercise search must pause before it asks the API.
// Short enough to feel immediate, long enough to skip most mid-word queries.
const EXERCISE_SEARCH_DELAY_MS = 250

// Moves focus to an input by id. `select` highlights its value, so typing
// replaces a copied figure instead of appending to it.
function focusInput(id: string, select = false) {
  const element = document.getElementById(id)
  if (!(element instanceof HTMLInputElement)) return
  element.focus()
  if (select) element.select()
}

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

// /workouts/new and /workouts/:id/edit render the same component. The key
// gives each route (and each workout) its own editor instance, so the state
// initialised from that route's stored draft never carries over to another.
//
// One exception: a new page's first autosave moves the address to its edit
// page (specs/003 D9). That is still the same page being written, so the
// address change carries the new page's key in router state, and the editor
// (with its focus, and the phone's keyboard) stays as it is.
const KEEP_NEW_PAGE_EDITOR = { editorKey: 'new' }

function readEditorKey(state: unknown): string | null {
  return typeof state === 'object' &&
    state !== null &&
    'editorKey' in state &&
    typeof state.editorKey === 'string'
    ? state.editorKey
    : null
}

export default function NewWorkout() {
  const { workoutId } = useParams()
  const location = useLocation()
  return (
    <WorkoutEditor key={readEditorKey(location.state) ?? workoutId ?? 'new'} />
  )
}

// A write refused because the page moved on (409 page_changed) or is gone
// (404): autosave stops, and the date line says which (specs/003 D10).
type PageProblem = 'conflict' | 'gone'

function pageProblemOf(error: unknown): PageProblem | null {
  if (!(error instanceof ApiError)) return null
  if (error.status === 409 && error.code === PAGE_CHANGED) return 'conflict'
  if (error.status === 404) return 'gone'
  return null
}

// What the server holds once a page is loaded (or reloaded after a conflict).
function describeServerPage(draft: ExistingWorkoutDraft) {
  const prepared = prepareAutosaveDraft(draft.heading, draft.exercises)
  return {
    heading: prepared.ok ? prepared.value.workout : null,
    exercises: prepared.ok
      ? JSON.stringify(prepared.value.exercises.exercises)
      : null,
    trigger: autosaveTrigger(draft.heading, draft.exercises),
  }
}

// What a save sends: the prepared page, and which editor block each sent
// block came from (an autosave leaves out blocks with no complete set).
interface DraftSnapshot {
  prepared: PreparedWorkoutDraft
  blockIndexes: number[]
  exercises: WorkoutExerciseDraft[]
}

function WorkoutEditor() {
  const navigate = useNavigate()
  const user = useRouteLoaderData('auth') as MeResponse
  const params = useParams()
  // Read once: after a new page's first save the address becomes its edit
  // page, but this stays the new page's editor (see KEEP_NEW_PAGE_EDITOR).
  const [workoutIdParam] = useState(params.workoutId)
  const parsedWorkoutId = Number(workoutIdParam)
  const isEditing = workoutIdParam !== undefined
  const hasValidWorkoutId =
    Number.isSafeInteger(parsedWorkoutId) && parsedWorkoutId > 0

  // Draft safety (editorDraftStorage.ts): the editor keeps a copy of its draft
  // in this tab's sessionStorage, so a reload, a locked phone or an expired
  // sign-in doesn't lose a half-logged session. The copy this route left is
  // read once, at mount, and wins over a fresh page or the server's copy.
  const [initialRoute] = useState<EditorDraftRoute | null>(() => {
    if (!isEditing) return { kind: 'new' }
    return hasValidWorkoutId
      ? { kind: 'edit', workoutId: parsedWorkoutId }
      : null
  })
  const [restoredDraft] = useState(() =>
    initialRoute === null ? null : loadEditorDraft(initialRoute, user.userId),
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
  // The trimmed query the shown suggestions (or error) answer. While it lags
  // behind what is typed, a search is waiting or in flight and the previous
  // answer stays on screen, so the picker doesn't collapse on every keystroke.
  const [answeredExerciseQuery, setAnsweredExerciseQuery] = useState('')
  const [exerciseSearchMessage, setExerciseSearchMessage] = useState<
    string | null
  >(null)
  const [savingAction, setSavingAction] = useState<
    'save' | 'finish' | 'tear-out' | 'keep' | null
  >(null)
  const [saveMessage, setSaveMessage] = useState<string | null>(null)
  const [isLoading, setIsLoading] = useState(isEditing)
  const [loadMessage, setLoadMessage] = useState<string | null>(null)
  const [confirmingFinish, setConfirmingFinish] = useState(false)
  // A page started long ago (or ahead of the clock) can't be finished "now"
  // honestly, so its finish question asks for the time instead. Decided when
  // the question opens, so it can't change shape under the lifter's thumb.
  const [finishAsksTime, setFinishAsksTime] = useState(false)
  const [finishTime, setFinishTime] = useState('')
  const [removal, setRemoval] = useState<Removal | null>(null)
  const [invalidField, setInvalidField] = useState<InvalidField | null>(null)
  // The page heading's fields start folded behind their one-line summary;
  // a save that fails on one of them opens it again.
  const [isHeadingOpen, setIsHeadingOpen] = useState(false)
  useVisualViewportHeight()
  const pickerRef = useRef<HTMLDivElement>(null)
  const keepFinishEditingRef = useRef<HTMLButtonElement>(null)
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
  // The same id for code running after an await, which would otherwise see
  // the value from the render it started in.
  const savedWorkoutIdRef = useRef(savedWorkoutId)

  // The stored copy's key: the route this editor opened on, except that a new
  // page moves to its edit page's key once it exists on the server (D9).
  const draftRoute = useMemo<EditorDraftRoute | null>(() => {
    if (isEditing) return initialRoute
    return savedWorkoutId === null
      ? { kind: 'new' }
      : { kind: 'edit', workoutId: savedWorkoutId }
  }, [initialRoute, isEditing, savedWorkoutId])

  // — Autosave (specs/003-durable-logging) —
  // A session still in progress saves itself: a new page, or an edit page
  // opened without a finish time. A finished page being corrected keeps the
  // explicit Save changes. Decided when the page opens, so typing a finish
  // time into the heading doesn't switch modes under the lifter's thumb.
  const [isInProgress, setIsInProgress] = useState(!isEditing)
  // The page revision this editor's changes are based on, sent with every
  // write as expectedRevision (FR-011). A restored copy brings the revision
  // it was made on.
  const revisionRef = useRef<number | null>(restoredDraft?.revision ?? null)
  // The revision of the page as loaded, for "Discard changes".
  const serverRevision = useRef<number | null>(null)
  // What the server holds, as last sent or loaded, so a save sends only the
  // half that changed: the heading's fields, and the blocks as JSON.
  const savedHeadingRef = useRef<CreateWorkoutRequest | null>(null)
  const savedExercisesRef = useRef<string | null>(null)
  // Exercises created by a save whose bodyweight choice hasn't been stored
  // yet; retried with the next save if the PATCH fails.
  const pendingBodyweightIds = useRef(new Set<number>())
  const [pageProblem, setPageProblem] = useState<PageProblem | null>(null)
  // The same, for code running after an await.
  const pageProblemRef = useRef(pageProblem)
  useEffect(() => {
    pageProblemRef.current = pageProblem
  })
  // After a conflict reload: what existed only in this tab (D10).
  const [unsavedHere, setUnsavedHere] = useState<string[] | null>(null)
  const [confirmingTearOut, setConfirmingTearOut] = useState(false)
  const keepPageRef = useRef<HTMLButtonElement>(null)
  // False once the editor has gone: a save finishing after that leaves the
  // screen alone.
  const isMounted = useRef(true)

  // A page deleted elsewhere has no page of its own to go back to.
  const cancelTarget =
    isEditing && pageProblem !== 'gone'
      ? `/workouts/${parsedWorkoutId}`
      : '/workouts'

  const isDirty =
    !isLoading && draftFingerprint({ heading, endTime, exercises }) !== baseline

  // Writes the stored copy while the draft differs from what the server has,
  // and removes it when it doesn't. Without consent the four optional details
  // are never stored (they're never shown or sent either). It records the
  // revision the changes are based on, so a restored copy still meets the
  // version check. No token: the session has ended and invalidation has
  // already cleared or held the copy; writing now would undo that.
  const detailsNotAllowed = optionalDetails.status === 'not-allowed'
  const writeStoredDraft = useCallback(
    (
      route: EditorDraftRoute,
      content: {
        heading: WorkoutHeadingDraft
        endTime: string
        exercises: WorkoutExerciseDraft[]
      },
      dirty: boolean,
    ) => {
      if (isDraftClosed.current || getToken() === null) return
      if (!dirty) {
        removeEditorDraft(route)
        return
      }
      saveEditorDraft(route, {
        ownerId: user.userId,
        savedWorkoutId: savedWorkoutIdRef.current,
        revision: revisionRef.current,
        heading: detailsNotAllowed
          ? dropOptionalDetails(content.heading)
          : content.heading,
        endTime: content.endTime,
        exercises: content.exercises,
      })
    },
    [detailsNotAllowed, user.userId],
  )

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
      void scheduler.stop()
      void navigate(cancelTarget)
      return
    }

    if (isEditing && serverDraft.current !== null) {
      setHeading(serverDraft.current.heading)
      setEndTime(serverDraft.current.endTime)
      setExercises(serverDraft.current.exercises)
      // Back to the server's page, so back to its revision too.
      revisionRef.current = serverRevision.current
    } else {
      const fresh = createInitialHeadingDraft(new Date())
      setBaseline(
        draftFingerprint({ heading: fresh, endTime: '', exercises: [] }),
      )
      setHeading(fresh)
      setEndTime('')
      setExercises([])
      setSavedWorkoutId(null)
      savedWorkoutIdRef.current = null
      revisionRef.current = null
      savedHeadingRef.current = null
      savedExercisesRef.current = null
      setLastSavedAt(null)
    }
  }

  // On the edit page the suggestions' "last time" must come from the session
  // before this one, never from a set this page has already logged.
  const excludeWorkoutId =
    isEditing && hasValidWorkoutId ? parsedWorkoutId : undefined

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
    if (document.activeElement !== searchRef.current) return
    pickerRef.current?.scrollIntoView({ block: 'nearest' })
  }, [answeredExerciseQuery, pickerResultCount])

  // The finish question replaces the buttons that opened it, so focus would
  // be lost with them: move it to the safe answer, and back to "Finish
  // session" when the question closes without finishing.
  const wasConfirmingFinish = useRef(false)
  useEffect(() => {
    // A question that asks for the time starts in its field instead.
    if (confirmingFinish) {
      if (finishAsksTime) focusInput(FINISH_TIME_ID)
      else keepFinishEditingRef.current?.focus()
    } else if (wasConfirmingFinish.current) {
      document.getElementById(FINISH_BUTTON_ID)?.focus()
    }
    wasConfirmingFinish.current = confirmingFinish
  }, [confirmingFinish, finishAsksTime])

  // Focusing the search box opens the keyboard, which shrinks the visible
  // area (see useVisualViewportHeight). Once it has settled, scroll the
  // picker back into view inside the editor's own scroller.
  function revealPicker() {
    window.setTimeout(() => {
      if (document.activeElement !== searchRef.current) return
      pickerRef.current?.scrollIntoView({ block: 'nearest' })
    }, 300)
  }

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
    if (invalidField.kind === 'set' && field.kind === 'set') {
      return (
        invalidField.setClientId === field.setClientId &&
        invalidField.field === field.field
      )
    }
    if (invalidField.kind === 'heading' && field.kind === 'heading') {
      return invalidField.field === field.field
    }
    // The finish-time field is the only one of its kind.
    return true
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

    if ('kind' in at && at.kind === 'finishTime') {
      flushSync(() => setInvalidField(at))
      focusInput(FINISH_TIME_ID)
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
    // "last time" on a new page and on a session still being logged; the
    // edit page's search leaves the page itself out (excludeWorkoutId), so
    // it's never one of this page's own sets. A finished page being
    // corrected gets none: its "last time" could be a later session.
    const isLogging = isInProgress
    const draft = createWorkoutExerciseDraft(
      createClientId(),
      createClientId(),
      exercise.id,
      exercise.name,
      exercise.isBodyweight,
      isLogging ? exercise.lastSet : null,
      isLogging ? exercise.firstSet : null,
    )

    addExerciseBlock(draft)
  }

  // A new block ends the Undo offer for a removed block, whose position
  // would otherwise be ambiguous.
  // Focus goes straight to the new block's first set, still inside the tap
  // that picked it (flushSync renders the row first), so the keyboard opens
  // on the figure to log. With "last time" written in, that figure is
  // selected: the same set again is one tap away, a new one is typed over it.
  function addExerciseBlock(draft: WorkoutExerciseDraft) {
    if (removal?.kind === 'exercise') setRemoval(null)
    flushSync(() => {
      setExercises((current) => [...current, draft])
      clearExercisePicker()
    })
    focusSetEntry(draft, draft.sets[0].clientId)
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
    setAnsweredExerciseQuery('')
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

  // — Saving —

  // The heading as a PATCH: only fields that differ from the server's, so
  // an unchanged heading costs no request. The four optional details are
  // left out entirely unless the account allows them: omitted keeps what the
  // server has, where null would clear it while consent is still loading.
  function headingChanges(
    workout: CreateWorkoutRequest,
  ): UpdateWorkoutRequest | null {
    const saved = savedHeadingRef.current
    const fields: (keyof CreateWorkoutRequest)[] = detailsAllowed
      ? ['date', 'startedAt', 'title', 'bodyweightKg', 'location', 'notes']
      : ['date', 'startedAt']
    const changes: UpdateWorkoutRequest = {}
    for (const field of fields) {
      if (saved === null || saved[field] !== workout[field]) {
        Object.assign(changes, { [field]: workout[field] })
      }
    }
    return Object.keys(changes).length === 0 ? null : changes
  }

  // Writes one snapshot of the draft (D1: the same bulk PUT as before, not
  // per-set routes). The first time, it creates the page. After that it
  // sends the heading only when it changed and the blocks only when they
  // changed, and a finish time when given (null reopens the page). Every
  // write carries the revision this editor holds and keeps the one that
  // comes back (FR-011), so a stale write is refused, not applied. A new
  // exercise saved as bodyweight gets that choice stored once it has an id.
  // Returns the page's id and the exercise ids it learned, by block client
  // id; throws what the API threw.
  async function persistDraft(
    snapshot: DraftSnapshot,
    endedAt?: string | null,
  ): Promise<{ workoutId: number; learnedIds: Map<string, number> }> {
    const { workout, exercises } = snapshot.prepared
    let workoutId = savedWorkoutIdRef.current
    const expected = () => revisionRef.current ?? undefined

    if (workoutId === null) {
      const created = await createWorkout(workout)
      workoutId = created.id
      savedWorkoutIdRef.current = created.id
      revisionRef.current = created.revision
      savedHeadingRef.current = workout
      savedExercisesRef.current = '[]'
      setSavedWorkoutId(created.id)
    }

    const learnedIds = new Map<string, number>()
    const blocks = JSON.stringify(exercises.exercises)
    if (blocks !== savedExercisesRef.current) {
      const saved = await replaceWorkoutExercises(workoutId, {
        ...exercises,
        expectedRevision: expected(),
      })
      revisionRef.current = saved.revision
      savedExercisesRef.current = blocks

      // New exercises start as loaded on the backend; the response supplies
      // their ids, so an ask-on-create bodyweight choice can stick. The ids
      // also go back into the draft, so the next save doesn't ask again.
      snapshot.blockIndexes.forEach((draftIndex, sentIndex) => {
        const draft = snapshot.exercises[draftIndex]
        const savedBlock = saved.exercises[sentIndex]
        if (draft.exerciseId !== null || savedBlock === undefined) return
        learnedIds.set(draft.clientId, savedBlock.exerciseId)
        if (draft.isBodyweight) {
          pendingBodyweightIds.current.add(savedBlock.exerciseId)
        }
      })
      if (learnedIds.size > 0 && isMounted.current) {
        setExercises((current) => withExerciseIds(current, learnedIds))
      }
    }

    await Promise.all(
      [...pendingBodyweightIds.current].map(async (exerciseId) => {
        await updateExerciseRecord(exerciseId, { isBodyweight: true })
        pendingBodyweightIds.current.delete(exerciseId)
      }),
    )

    const changes = headingChanges(workout)
    if (changes !== null || endedAt !== undefined) {
      const updated = await updateWorkout(workoutId, {
        ...changes,
        ...(endedAt === undefined ? {} : { endedAt }),
        expectedRevision: expected(),
      })
      revisionRef.current = updated.revision
      savedHeadingRef.current = { ...workout }
    }

    return { workoutId, learnedIds }
  }

  function currentDraftRoute(): EditorDraftRoute | null {
    if (isEditing) return initialRoute
    const id = savedWorkoutIdRef.current
    return id === null ? { kind: 'new' } : { kind: 'edit', workoutId: id }
  }

  // Consent was withdrawn elsewhere while this page was open, and the server
  // refused the details (specs/001 contracts/ui.md → Rejected save). Not a
  // session problem: keep the draft, drop only those details, say so, and
  // offer the opt-in entry again.
  function dropWithdrawnDetails() {
    setHeading(dropOptionalDetails)
    optionalDetails.setStatus('not-allowed')
    setSaveMessage(
      isInProgress
        ? 'This account no longer allows a title, bodyweight, gym or notes, so they were removed from this page and not saved. The rest of your page is kept.'
        : 'This account no longer allows a title, bodyweight, gym or notes, so they were removed from this page and not saved. The rest of your draft is kept; save again.',
    )
  }

  const { scheduler, saveState, lastSavedAt, setLastSavedAt, seedTrigger } =
    useWorkoutAutosave({
      content: { heading, endTime, exercises },
      detailsAllowed,
      isInProgress,
      isLoading,
      loadMessage,
      savedWorkoutIdRef,
      isMountedRef: isMounted,
      persistDraft,
      currentDraftRoute,
      writeStoredDraft,
      setBaseline,
      setRestoredAt,
      onPageProblem(error) {
        const problem = pageProblemOf(error)
        if (problem === null) return false
        if (isMounted.current) setPageProblem(problem)
        return true
      },
      dropWithdrawnDetails,
    })

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
          const known = describeServerPage(draft)
          savedHeadingRef.current = known.heading
          savedExercisesRef.current = known.exercises
          seedTrigger(known.trigger)
          // A restored copy keeps the revision it was made on, so if the
          // page has moved on since, its save is refused rather than
          // overwriting the other device's sets. A copy from before the
          // revision existed can only take the server's.
          serverRevision.current = workout.revision
          revisionRef.current = restoredDraft?.revision ?? workout.revision
          // Unsaved changes from before a reload or sign-in win over the
          // server's copy; "Discard changes" goes back to it.
          const shown = restoredDraft ?? draft
          setHeading(shown.heading)
          setEndTime(shown.endTime)
          setExercises(shown.exercises)
          setRestoredAt(restoredDraft?.savedAt ?? null)
          setSavedWorkoutId(workout.id)
          setIsInProgress(draft.endTime === '')

          // A session still in progress shows "last time" on the blocks it
          // already has, as it does on blocks picked now; a finished page
          // being corrected shows none, since its "last time" could be a
          // later session. One request covers every block. It is only a
          // hint, so a failure leaves the blocks without it and says nothing.
          if (draft.endTime === '') {
            try {
              const found = await searchExercises('', workout.id)
              if (!cancelled) {
                setExercises((current) => withLastTime(current, found))
              }
            } catch {
              // No hint is the same as before this request existed.
            }
          }
        }
      } catch (error: unknown) {
        if (!cancelled) {
          // The page is gone (deleted elsewhere, or never this user's), so a
          // stored copy of changes to it has nowhere to go. Any other failure
          // keeps the copy for the next try.
          if (
            error instanceof ApiError &&
            error.status === 404 &&
            initialRoute !== null
          ) {
            removeEditorDraft(initialRoute)
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
  }, [
    hasValidWorkoutId,
    initialRoute,
    isEditing,
    parsedWorkoutId,
    restoredDraft,
    seedTrigger,
  ])

  // Keeps the stored copy in step with every edit. lastSavedAt is here so a
  // save, which moves the revision on, rewrites it too.
  useEffect(() => {
    if (draftRoute === null || isLoading || loadMessage !== null) return
    writeStoredDraft(draftRoute, { heading, endTime, exercises }, isDirty)
  }, [
    draftRoute,
    endTime,
    exercises,
    heading,
    isDirty,
    isLoading,
    lastSavedAt,
    loadMessage,
    writeStoredDraft,
  ])

  // A first save moved the new page onto the server: its address and its
  // stored copy move to the edit page (D9), without a new history entry, so
  // a reload or Continue logging opens this same page.
  useEffect(() => {
    if (isEditing || savedWorkoutId === null || isDraftClosed.current) return
    removeEditorDraft({ kind: 'new' })
    void navigate(`/workouts/${savedWorkoutId}/edit`, {
      replace: true,
      state: KEEP_NEW_PAGE_EDITOR,
    })
  }, [isEditing, navigate, savedWorkoutId])

  // Finish session flushes any pending save first (FR-007), so the question
  // is asked about a page the server already has. A conflict or a deleted
  // page found on the way is shown instead of the question.
  async function openFinishQuestion() {
    await scheduler.flush()
    if (!isMounted.current || pageProblemRef.current !== null) return
    setFinishAsksTime(needsExplicitFinishTime(heading, new Date()))
    setFinishTime('')
    setConfirmingFinish(true)
  }
  // A finish-time problem belongs to the question, so it goes with it.
  function closeFinishQuestion() {
    if (invalidField?.kind === 'finishTime') {
      setInvalidField(null)
      setSaveMessage(null)
    }
    setConfirmingFinish(false)
  }

  // The explicit save: Confirm finish, and Save changes on a finished page.
  // Unlike autosave it takes every row, so a half-filled one must be fixed
  // or removed, and the field is pointed at.
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

    // Capture the confirmation instant, not the later instant after network
    // I/O — unless the question asked when the session ended, in which case
    // that answer on the page's own date is the end (needsExplicitFinishTime).
    // Left undefined, the finish time isn't touched.
    let endedAt: string | null | undefined = finishSession
      ? new Date().toISOString()
      : undefined
    if (finishSession && finishAsksTime) {
      endedAt = createLocalEndedAt(
        prepared.value.workout.date,
        heading.startTime,
        finishTime,
      )
      if (endedAt === null) {
        setSaveMessage('Enter the time the session finished.')
        pointAtProblem({ kind: 'finishTime' })
        return
      }
    }
    // A finished page's Finished field: a time, or empty to reopen it.
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
    } else if (isEditing && !finishSession) {
      endedAt = null
    }

    setSavingAction(finishSession ? 'finish' : 'save')
    setSaveMessage(null)
    setInvalidField(null)
    // No autosave may run alongside: two writes with one revision would
    // refuse each other.
    await scheduler.stop()

    try {
      const { workoutId } = await persistDraft(
        {
          prepared: prepared.value,
          blockIndexes: exercises.map((_, index) => index),
          exercises,
        },
        endedAt,
      )

      // Saved: the stored copy has done its job.
      isDraftClosed.current = true
      const route = currentDraftRoute()
      if (route !== null) removeEditorDraft(route)
      removeEditorDraft({ kind: 'new' })
      // The screen it lands on confirms the save, so leaving the editor never
      // leaves the lifter guessing whether the sets made it.
      const notice = finishSession
        ? 'Session finished and saved.'
        : 'Changes saved.'
      void navigate(
        finishSession && !isEditing ? '/workouts' : `/workouts/${workoutId}`,
        { state: routeNotice(notice) },
      )
    } catch (error: unknown) {
      const problem = pageProblemOf(error)
      if (problem !== null) {
        setPageProblem(problem)
        setConfirmingFinish(false)
        return
      }
      if (isOptionalDetailsConsentRequired(error)) {
        dropWithdrawnDetails()
      } else {
        setSaveMessage(
          savedWorkoutIdRef.current === null
            ? 'The page could not be saved. Please try again.'
            : 'The page was started but not fully saved. Your draft is still here; try again.',
        )
      }
      // Autosave picks up again from what the server has.
      scheduler.resume()
      if (isInProgress) scheduler.edit()
    } finally {
      setSavingAction(null)
    }
  }

  // Cancel on a new page that has already saved itself asks whether to tear
  // it out (Story 4): the page is on the server, so leaving can't quietly
  // drop it any more, and Cancel mustn't be able to lose a real session.
  // Not after a conflict: tearing out would take the other device's sets too.
  const keepsPageOnCancel =
    !isEditing && savedWorkoutId !== null && pageProblem === null

  useEffect(() => {
    if (confirmingTearOut) keepPageRef.current?.focus()
  }, [confirmingTearOut])

  // Keep: whatever is still waiting is saved first, then on to Sessions,
  // where the page is in progress. If that save fails the lifter stays, with
  // the date line saying so, rather than leaving sets behind unknowingly.
  async function keepPage() {
    setSavingAction('keep')
    const flushed = await scheduler.flush()
    if (!isMounted.current) return
    setSavingAction(null)
    if (!flushed) {
      setConfirmingTearOut(false)
      if (pageProblemRef.current === null) {
        setSaveMessage(
          'The latest sets could not be saved yet. They are kept here; try again in a moment.',
        )
      }
      return
    }
    void navigate('/workouts', {
      state: routeNotice('Page kept. It stays open for more sets.'),
    })
  }

  async function tearOutPage() {
    const workoutId = savedWorkoutIdRef.current
    if (workoutId === null) return
    setSavingAction('tear-out')
    setSaveMessage(null)
    await scheduler.stop()
    try {
      await deleteWorkout(workoutId)
    } catch (error: unknown) {
      // Already gone (deleted on another device) is what was asked for.
      if (!(error instanceof ApiError && error.status === 404)) {
        setSavingAction(null)
        setSaveMessage('The page could not be torn out. Please try again.')
        scheduler.resume()
        scheduler.edit()
        return
      }
    }
    isDraftClosed.current = true
    removeEditorDraft({ kind: 'edit', workoutId })
    removeEditorDraft({ kind: 'new' })
    void navigate('/workouts', { state: routeNotice('Page torn out.') })
  }

  // Reload page, after a conflict (D10): the server's page replaces the
  // draft, and the sets that existed only here are listed rather than
  // merged, so nothing vanishes without a trace.
  async function reloadPage() {
    const workoutId = savedWorkoutIdRef.current
    if (workoutId === null) return
    setSaveMessage(null)
    try {
      const workout = await getWorkout(workoutId)
      if (!isMounted.current) return
      const draft = createExistingWorkoutDraft(workout, createClientId)
      // Keep the "last time" lines the blocks already showed.
      const lastSets = new Map(
        exercises.map((exercise) => [exercise.exerciseId, exercise.lastSet]),
      )
      const reloaded = draft.exercises.map((exercise) =>
        lastSets.has(exercise.exerciseId)
          ? { ...exercise, lastSet: lastSets.get(exercise.exerciseId) }
          : exercise,
      )
      const lost = describeUnsavedSets(exercises, draft.exercises)

      serverDraft.current = draft
      serverRevision.current = workout.revision
      revisionRef.current = workout.revision
      const known = describeServerPage(draft)
      savedHeadingRef.current = known.heading
      savedExercisesRef.current = known.exercises
      seedTrigger(known.trigger)
      setHeading(draft.heading)
      setEndTime(draft.endTime)
      setExercises(reloaded)
      setBaseline(draftFingerprint({ ...draft, exercises: reloaded }))
      setIsInProgress(draft.endTime === '')
      setRestoredAt(null)
      setRemoval(null)
      setInvalidField(null)
      setLastSavedAt(null)
      setPageProblem(null)
      setUnsavedHere(lost.length === 0 ? null : lost)
      scheduler.resume()
    } catch (error: unknown) {
      if (pageProblemOf(error) === 'gone') {
        setPageProblem('gone')
      } else {
        setSaveMessage('The page could not be reloaded. Please try again.')
      }
    }
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
  const isSaving = savingAction !== null
  // Shown on the folded heading line only when the account allows it.
  const summaryTitle = detailsAllowed ? heading.title.trim() : ''

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
            // The page no longer exists: the copy kept for reading has
            // nowhere to go, so leaving drops it.
            if (pageProblem === 'gone') {
              isDraftClosed.current = true
              if (draftRoute !== null) removeEditorDraft(draftRoute)
              return
            }
            // A new page already on the server asks whether to tear it out.
            if (keepsPageOnCancel) {
              event.preventDefault()
              setConfirmingFinish(false)
              setConfirmingTearOut(true)
              return
            }
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
            {unsavedHere !== null && (
              <p className="new-workout-restored" role="status">
                <span>Not saved here: {unsavedHere.join('; ')}.</span>
                <button type="button" onClick={() => setUnsavedHere(null)}>
                  Dismiss
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
              {/* The save state (contracts/ui.md). Outside the button, so the
                  button's name stays the date; a polite live region that is
                  always there, so a change in it is announced. A conflict or
                  a deleted page needs action and is announced at once. */}
              {pageProblem === null ? (
                <p
                  className={`new-workout-save-state${saveState?.isError === true ? ' is-error' : ''}`}
                  role="status"
                >
                  {saveState?.text}
                </p>
              ) : (
                <p className="new-workout-save-state is-error" role="alert">
                  {pageProblem === 'conflict'
                    ? 'This page changed on another device.'
                    : 'This page no longer exists.'}
                  {pageProblem === 'conflict' && (
                    <button type="button" onClick={() => void reloadPage()}>
                      Reload page
                    </button>
                  )}
                </p>
              )}
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
                  {/* Only on a finished page: an in-progress one is finished
                      with Finish session, which saves; a time typed here
                      would never be. */}
                  {isEditing && !isInProgress && (
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
                                enterKeyHint="next"
                                aria-label={`${exercise.exerciseName}, set ${setIndex + 1}, weight`}
                                {...invalidProps({
                                  kind: 'set',
                                  setClientId: set.clientId,
                                  field: 'weight',
                                })}
                                {...selectOnFocusProps}
                                value={set.weight}
                                onChange={(event) =>
                                  changeSet(exercise.clientId, set.clientId, {
                                    weight: event.target.value,
                                  })
                                }
                                onKeyDown={(event) =>
                                  advanceFromSetField(
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
                              {...invalidProps({
                                kind: 'set',
                                setClientId: set.clientId,
                                field: 'reps',
                              })}
                              {...selectOnFocusProps}
                              value={set.reps}
                              onChange={(event) =>
                                changeSet(exercise.clientId, set.clientId, {
                                  reps: event.target.value,
                                })
                              }
                              onKeyDown={(event) =>
                                advanceFromSetField(
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
                  <p
                    className="form-message exercise-search-status"
                    role="alert"
                  >
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
                          Not in the list? Add{' '}
                          <strong>{answeredExerciseQuery}</strong> as a new
                          exercise:
                        </>
                      ) : (
                        <>
                          No exercise called{' '}
                          <strong>{answeredExerciseQuery}</strong> yet. Add it
                          as:
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
            </section>
          </div>

          <footer className="new-workout-actions">
            {saveMessage !== null && (
              <p id={SAVE_MESSAGE_ID} className="form-message" role="alert">
                {saveMessage}
              </p>
            )}
            {/* The confirmation takes the place of the action buttons rather
                than stacking above them, so there is only one question and
                one pair of buttons on screen. */}
            {confirmingTearOut ? (
              <div className="finish-confirmation" role="alert">
                <p>{describeTearOut(countDraftSets(exercises))}</p>
                <div>
                  <button
                    ref={keepPageRef}
                    className="btn btn-ghost"
                    type="button"
                    onClick={() => void keepPage()}
                  >
                    Keep page
                  </button>
                  <button
                    className="btn btn-secondary"
                    type="button"
                    onClick={() => void tearOutPage()}
                  >
                    {savingAction === 'tear-out'
                      ? 'Tearing out…'
                      : 'Tear out page'}
                  </button>
                </div>
              </div>
            ) : confirmingFinish ? (
              <div className="finish-confirmation" role="alert">
                {finishAsksTime ? (
                  <>
                    <label htmlFor={FINISH_TIME_ID}>
                      This page started{' '}
                      <span className="num">
                        {/* "today 07.15" mid-sentence, "30 Sep 07.15" otherwise */}
                        {describeHeadingWhen(heading, '', new Date()).replace(
                          /^Today/,
                          'today',
                        )}
                      </span>
                      . When did the session finish?
                    </label>
                    <input
                      className="input num"
                      id={FINISH_TIME_ID}
                      type="time"
                      required
                      {...invalidProps({ kind: 'finishTime' })}
                      value={finishTime}
                      onChange={(event) => {
                        if (isInvalid({ kind: 'finishTime' })) {
                          setInvalidField(null)
                          setSaveMessage(null)
                        }
                        setFinishTime(event.target.value)
                      }}
                    />
                  </>
                ) : (
                  <p>
                    Finish this session now? The current time will be saved.
                  </p>
                )}
                <div>
                  <button
                    className="btn btn-ghost"
                    type="button"
                    onClick={closeFinishQuestion}
                    ref={keepFinishEditingRef}
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
            ) : (
              <div className="new-workout-action-buttons">
                {/* One action. In progress: Finish session, with no Save
                    button, since the page saves itself (owner, 2026-10-07).
                    Finished: Save changes, as before autosave. */}
                {isInProgress ? (
                  <button
                    className="btn btn-primary"
                    type="button"
                    id={FINISH_BUTTON_ID}
                    onClick={() => void openFinishQuestion()}
                  >
                    {savingAction === 'finish'
                      ? 'Finishing…'
                      : 'Finish session'}
                  </button>
                ) : (
                  <button
                    className="btn btn-primary"
                    type="button"
                    onClick={() => void saveWorkout(false)}
                  >
                    {savingAction === 'save' ? 'Saving…' : 'Save changes'}
                  </button>
                )}
              </div>
            )}
          </footer>
        </fieldset>
      </form>
    </main>
  )
}
