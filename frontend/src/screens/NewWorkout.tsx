import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
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
  type WorkoutDetailResponse,
} from '../api/workouts'
import {
  countNotDone,
  type DraftFieldRef,
  describeUnsavedSets,
  type ExistingWorkoutDraft,
  createExistingWorkoutDraft,
  createInitialHeadingDraft,
  createLocalEndedAt,
  createWorkoutExerciseDraft,
  describeHeadingWhen,
  dropOptionalDetails,
  findNextSet,
  needsExplicitFinishTime,
  prepareAutosaveDraft,
  prepareWorkoutDraft,
  type PreparedWorkoutDraft,
  toggleSetDone,
  withLastTime,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
  type WorkoutSetDraft,
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
import { ExerciseBlockList } from './ExerciseBlockList'
import { ExercisePicker } from './ExercisePicker'
import { OptionalDetailsChoice } from './OptionalDetailsConsent'
import { formatCount } from './workoutFormat'
import {
  autosaveTrigger,
  useWorkoutAutosave,
  withExerciseIds,
} from './useWorkoutAutosave'
import { focusInput, setFieldId, useExerciseBlocks } from './useExerciseBlocks'
import { useOptionalDetailsAllowed } from './useOptionalDetailsAllowed'
import { useVisualViewportHeight } from './useVisualViewportHeight'
import './NewWorkout.css'

// A field a failed save points at, by the set's client id rather than its
// position, so the mark stays on the right row while rows come and go.
type InvalidField =
  | {
      kind: 'heading'
      field: 'date' | 'startTime' | 'bodyweightKg' | 'endTime'
    }
  | { kind: 'set'; setClientId: string; field: 'weight' | 'reps' }
  | { kind: 'finishTime' }

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
const FINISH_BUTTON_ID = 'new-workout-finish'
const FINISH_TIME_ID = 'new-workout-finish-time'
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

// What the server holds once a page is loaded (or reloaded after a conflict),
// read the way autosave sends it, so an unchanged half is never sent again.
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
  const [invalidField, setInvalidField] = useState<InvalidField | null>(null)
  // The page heading's fields start folded behind their one-line summary;
  // a save that fails on one of them opens it again.
  const [isHeadingOpen, setIsHeadingOpen] = useState(false)
  useVisualViewportHeight()
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
    if (savedWorkoutId !== null)
      return { kind: 'edit', workoutId: savedWorkoutId }
    return { kind: 'new' }
  }, [initialRoute, isEditing, savedWorkoutId])

  // Blocks and set rows: add, change, remove and Undo (useExerciseBlocks.ts).
  // Editing a set the last save complained about takes its mark off, and the
  // message with it: it no longer describes what's on screen.
  const blocks = useExerciseBlocks({
    exercises,
    setExercises,
    // A row added while logging starts done, so logging as you go costs no
    // extra tap (research R14, Q5).
    newRowStatus: 'done',
    onSetChange(setClientId) {
      if (
        invalidField?.kind === 'set' &&
        invalidField.setClientId === setClientId
      ) {
        setInvalidField(null)
        setSaveMessage(null)
      }
    },
  })

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
    blocks.setRemoval(null)
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

  // Editing a field the last save complained about takes its mark off, and
  // the message with it: it no longer describes what's on screen.
  function updateHeadingField(field: keyof WorkoutHeadingDraft, value: string) {
    if (invalidField?.kind === 'heading' && invalidField.field === field) {
      setInvalidField(null)
      setSaveMessage(null)
    }
    setHeading((current) => ({ ...current, [field]: value }))
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
    // it's never one of this page's own sets. A finished page being corrected
    // gets none: its "last time" could be a later session.
    const draft = createWorkoutExerciseDraft(
      createClientId(),
      createClientId(),
      exercise.id,
      exercise.name,
      exercise.isBodyweight,
      isInProgress ? exercise.lastSet : null,
      isInProgress ? exercise.firstSet : null,
    )

    blocks.addExerciseBlock(draft)
  }

  // A name not in the index: it becomes an exercise when the page is saved.
  function selectNewExercise(name: string, isBodyweight: boolean) {
    const draft = createWorkoutExerciseDraft(
      createClientId(),
      createClientId(),
      null,
      name,
      isBodyweight,
      null,
      null,
    )

    blocks.addExerciseBlock(draft)
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
    const fields: (keyof CreateWorkoutRequest)[] = [
      'date',
      'startedAt',
      ...(detailsAllowed
        ? (['title', 'bodyweightKg', 'location', 'notes'] as const)
        : []),
    ]
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
          // being corrected shows none, since its "last time" could be a later session. One
          // request covers every block. It is only a hint, so a failure
          // leaves the blocks without it and says nothing.
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
      adoptServerPage(workout)
    } catch (error: unknown) {
      if (pageProblemOf(error) === 'gone') {
        setPageProblem('gone')
      } else {
        setSaveMessage('The page could not be reloaded. Please try again.')
      }
    }
  }

  // The server's page replaces the draft. Its finish time picks the mode
  // again, so a page finished elsewhere opens as finished here too.
  function adoptServerPage(workout: WorkoutDetailResponse) {
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
    blocks.setRemoval(null)
    setInvalidField(null)
    setLastSavedAt(null)
    setPageProblem(null)
    setUnsavedHere(lost.length === 0 ? null : lost)
    scheduler.resume()
  }

  const isSaving = savingAction !== null
  // Shown on the folded heading line only when the account allows it.
  const summaryTitle = detailsAllowed ? heading.title.trim() : ''
  // Set status (specs/006). While logging, the first planned set is "next";
  // a finished page has none, its not-done sets are skipped. Finishing keeps
  // whatever is still planned as skipped, and the question says how many.
  const nextSet = isInProgress ? findNextSet(exercises) : null
  const notDoneCount = countNotDone(exercises)
  const finishSkips =
    notDoneCount === 0
      ? null
      : `${formatCount(notDoneCount, 'planned set')} will be kept as skipped.`
  // The word under a skipped row on a finished page, so its state never rests
  // on the control's look alone. Planned sets need none: their dashed fields
  // and hollow toggle already say "not yet", and the set the lifter is up to
  // is marked by its toggle's accent ring.
  const setNote = (set: WorkoutSetDraft): string | null =>
    set.status === 'skipped' ? 'skipped' : null

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

              <ExerciseBlockList
                exercises={exercises}
                blocks={blocks}
                invalidProps={(setClientId, field) =>
                  invalidProps({ kind: 'set', setClientId, field })
                }
                done={{
                  nextSetClientId: nextSet?.setClientId ?? null,
                  note: setNote,
                  // Released, a set goes back to planned, or to skipped on a
                  // finished page (toggleSetDone).
                  onToggle: (exercise, set) =>
                    blocks.changeSet(exercise.clientId, set.clientId, {
                      status: toggleSetDone(set.status, !isInProgress),
                    }),
                }}
              />

              <ExercisePicker
                inputRef={searchRef}
                excludeWorkoutId={excludeWorkoutId}
                onSelect={selectExercise}
                onSelectNew={selectNewExercise}
              />
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
                    {finishSkips !== null && <p>{finishSkips}</p>}
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
                    {finishSkips === null
                      ? 'Finish this session now?'
                      : finishSkips}{' '}
                    The current time will be saved.
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
