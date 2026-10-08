import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type RefObject,
} from 'react'
import { isOptionalDetailsConsentRequired } from '../api/client'
import { getToken } from '../auth/token'
import {
  countDraftSets,
  draftFingerprint,
  removeEditorDraft,
  type EditorDraftRoute,
} from './editorDraftStorage'
import {
  describeIncompleteSet,
  dropOptionalDetails,
  findIncompleteSet,
  prepareAutosaveDraft,
  type PreparedWorkoutDraft,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
} from './newWorkoutDraft'
import {
  createIncompleteRowWatch,
  createSaveScheduler,
  type SaveOutcome,
  type SaveStatus,
} from './saveScheduler'
import { formatWorkoutTime } from './workoutFormat'

interface DraftContent {
  heading: WorkoutHeadingDraft
  endTime: string
  exercises: WorkoutExerciseDraft[]
}

interface AutosaveTrigger {
  fingerprint: string
  setCount: number
}

export function autosaveTrigger(
  heading: WorkoutHeadingDraft,
  exercises: WorkoutExerciseDraft[],
): AutosaveTrigger {
  const prepared = prepareAutosaveDraft(heading, exercises)
  return prepared.ok
    ? {
        fingerprint: JSON.stringify(prepared.value),
        setCount: prepared.setCount,
      }
    : { fingerprint: `invalid: ${prepared.message}`, setCount: 0 }
}

// A save gives newly created exercises server ids by block client id.
export function withExerciseIds(
  exercises: WorkoutExerciseDraft[],
  ids: Map<string, number>,
): WorkoutExerciseDraft[] {
  return exercises.map((exercise) => {
    const exerciseId = ids.get(exercise.clientId)
    return exercise.exerciseId === null && exerciseId !== undefined
      ? { ...exercise, exerciseId }
      : exercise
  })
}

interface AutosaveOptions {
  content: DraftContent
  detailsAllowed: boolean
  isInProgress: boolean
  isLoading: boolean
  loadMessage: string | null
  savedWorkoutIdRef: RefObject<number | null>
  isMountedRef: RefObject<boolean>
  persistDraft: (snapshot: {
    prepared: PreparedWorkoutDraft
    blockIndexes: number[]
    exercises: WorkoutExerciseDraft[]
  }) => Promise<{ workoutId: number; learnedIds: Map<string, number> }>
  currentDraftRoute: () => EditorDraftRoute | null
  writeStoredDraft: (
    route: EditorDraftRoute,
    content: DraftContent,
    dirty: boolean,
  ) => void
  setBaseline: (fingerprint: string) => void
  setRestoredAt: (value: string | null) => void
  onPageProblem: (error: unknown) => boolean
  dropWithdrawnDetails: () => void
}

// Owns the in-progress page's save clock and its date-line feedback. The
// editor supplies the shared write, which explicit Save and Finish also use.
export function useWorkoutAutosave({
  content,
  detailsAllowed,
  isInProgress,
  isLoading,
  loadMessage,
  savedWorkoutIdRef,
  isMountedRef,
  persistDraft,
  currentDraftRoute,
  writeStoredDraft,
  setBaseline,
  setRestoredAt,
  onPageProblem,
  dropWithdrawnDetails,
}: AutosaveOptions) {
  const lastTrigger = useRef<AutosaveTrigger>({ fingerprint: '', setCount: 0 })
  const [saveStatus, setSaveStatus] = useState<SaveStatus>({ kind: 'idle' })
  const [lastSavedAt, setLastSavedAt] = useState<string | null>(null)
  const [autosaveProblem, setAutosaveProblem] = useState<string | null>(null)

  // A delayed save reads the latest page, including edits made during debounce.
  const latestDraft = useRef(content)
  useEffect(() => {
    latestDraft.current = content
  })

  async function autosave(): Promise<SaveOutcome> {
    if (getToken() === null) return 'failed'
    const snapshot = latestDraft.current
    const prepared = prepareAutosaveDraft(
      detailsAllowed ? snapshot.heading : dropOptionalDetails(snapshot.heading),
      snapshot.exercises,
    )
    if (!prepared.ok) {
      setAutosaveProblem(prepared.message)
      return 'saved'
    }
    setAutosaveProblem(null)
    if (savedWorkoutIdRef.current === null && prepared.setCount === 0) {
      return 'saved'
    }

    const isNewPage = savedWorkoutIdRef.current === null
    let learnedIds: Map<string, number>
    try {
      const saved = await persistDraft({
        prepared: prepared.value,
        blockIndexes: prepared.blockIndexes,
        exercises: snapshot.exercises,
      })
      learnedIds = saved.learnedIds
    } catch (error: unknown) {
      if (onPageProblem(error)) return 'stopped'
      if (isOptionalDetailsConsentRequired(error) && isMountedRef.current) {
        dropWithdrawnDetails()
      }
      return 'failed'
    }
    if (!isMountedRef.current) return 'saved'

    setLastSavedAt(new Date().toISOString())
    setRestoredAt(null)
    const coveredAll =
      prepared.blockIndexes.length === snapshot.exercises.length &&
      prepared.setCount === countDraftSets(snapshot.exercises)
    const savedFingerprint = coveredAll
      ? draftFingerprint({
          ...snapshot,
          exercises: withExerciseIds(snapshot.exercises, learnedIds),
        })
      : null
    if (savedFingerprint !== null) setBaseline(savedFingerprint)

    // Update the stored copy immediately with the new revision. Keep page
    // may navigate before the effect in the editor gets another render.
    const latest = latestDraft.current
    const route = currentDraftRoute()
    if (isNewPage) removeEditorDraft({ kind: 'new' })
    if (route !== null) {
      writeStoredDraft(
        route,
        latest,
        savedFingerprint === null ||
          draftFingerprint({
            ...latest,
            exercises: withExerciseIds(latest.exercises, learnedIds),
          }) !== savedFingerprint,
      )
    }
    return 'saved'
  }

  const autosaveRef = useRef(autosave)
  useEffect(() => {
    autosaveRef.current = autosave
  })
  // The scheduler is created once; its callback reads the latest save through
  // the ref after every render, instead of retaining the first draft.
  // eslint-disable-next-line react-hooks/refs
  const [scheduler] = useState(() =>
    createSaveScheduler({
      save: () => autosaveRef.current(),
      onStatus: setSaveStatus,
    }),
  )

  useEffect(() => {
    isMountedRef.current = true
    scheduler.resume()
    return () => {
      isMountedRef.current = false
      void scheduler.stop()
    }
  }, [isMountedRef, scheduler])

  useEffect(() => {
    function onVisibilityChange() {
      if (document.visibilityState === 'hidden') void scheduler.flush()
    }
    document.addEventListener('visibilitychange', onVisibilityChange)
    return () =>
      document.removeEventListener('visibilitychange', onVisibilityChange)
  }, [scheduler])

  const autosaves = isInProgress && !isLoading && loadMessage === null
  const trigger = useMemo(
    () =>
      autosaveTrigger(
        detailsAllowed ? content.heading : dropOptionalDetails(content.heading),
        content.exercises,
      ),
    [content.exercises, content.heading, detailsAllowed],
  )
  useEffect(() => {
    if (!autosaves) return
    const previous = lastTrigger.current
    if (trigger.fingerprint === previous.fingerprint) return
    lastTrigger.current = trigger
    scheduler.edit({ immediate: trigger.setCount > previous.setCount })
  }, [autosaves, scheduler, trigger])

  const incompleteSet = autosaves ? findIncompleteSet(content.exercises) : null
  const incompleteKey =
    incompleteSet === null
      ? null
      : `${incompleteSet.setClientId}:${incompleteSet.needs}`
  const [dueIncompleteKey, setDueIncompleteKey] = useState<string | null>(null)
  const [incompleteWatch] = useState(() =>
    createIncompleteRowWatch(setDueIncompleteKey),
  )
  useEffect(() => {
    incompleteWatch.observe(incompleteKey)
  }, [incompleteKey, incompleteWatch])
  useEffect(() => () => incompleteWatch.dispose(), [incompleteWatch])

  const incompleteNotice =
    incompleteSet !== null && dueIncompleteKey === incompleteKey
      ? describeIncompleteSet(incompleteSet)
      : null
  let saveState: { text: string; isError: boolean } | null = null
  if (autosaveProblem !== null) {
    saveState = { text: `Not saved: ${autosaveProblem}`, isError: true }
  } else if (saveStatus.kind === 'failed') {
    saveState = { text: 'Not saved — retrying', isError: true }
  } else if (saveStatus.kind === 'saving' && saveStatus.slow) {
    saveState = { text: 'Saving…', isError: false }
  } else if (incompleteNotice !== null) {
    saveState = { text: incompleteNotice, isError: false }
  } else if (lastSavedAt !== null) {
    saveState = {
      text: `Saved ${formatWorkoutTime(lastSavedAt)}`,
      isError: false,
    }
  }

  const seedTrigger = useCallback((trigger: AutosaveTrigger) => {
    lastTrigger.current = trigger
  }, [])

  return {
    scheduler,
    saveState,
    lastSavedAt,
    setLastSavedAt,
    seedTrigger,
  }
}
