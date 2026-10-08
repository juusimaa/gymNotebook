import {
  dropOptionalDetails,
  type WorkoutExerciseDraft,
  type WorkoutHeadingDraft,
  type WorkoutSetDraft,
} from './newWorkoutDraft'
import {
  formatCount,
  formatWorkoutLongDate,
  formatWorkoutTime,
} from './workoutFormat'

// The session editor's draft lives in React state, which a phone lock, a tab
// eviction or a reload throws away. Losing a half-logged session that way
// breaks "Nothing is lost by accident" (PRODUCT.md, principle 4), so the editor
// also keeps a copy here, in sessionStorage (docs/ui/README.md → Session
// editor → Draft safety):
//
//   - sessionStorage, not localStorage: the copy belongs to this tab. It
//     survives a reload and a discarded tab, and goes when the tab is closed,
//     so nothing lingers on a shared device and two tabs never fight over one
//     draft. docs/privacy/processing-decision.md P5 records the key.
//   - One key per route: the new page, and each workout's edit page.
//   - Tagged with the owner's user id. A copy from another account is never
//     restored and is removed as soon as someone signs in.
//   - Cleared when the session ends, with one exception. The token lives 30
//     minutes and renews while the app is in use, up to 12 hours from sign-in
//     (PLAN.md → Auth). A phone asleep past expiry, or a session past the cap,
//     can still meet an expired token on its next save. That 401 *holds* the
//     draft instead, and it comes back once the same user signs in again.
//     Sign-out, another tab signing out, account deletion and every other 401
//     clear it.
//
// Storage can be missing or throw (private mode, blocked site data). Every
// access is wrapped: without storage the editor works exactly as before, it
// just can't survive a reload.

const KEY_PREFIX = 'gymnotebook.draft.'

// Bumped when the stored shape changes; a copy with another version is dropped
// rather than migrated, since it lives no longer than a tab.
const STORAGE_VERSION = 1

export type EditorDraftRoute =
  { kind: 'new' } | { kind: 'edit'; workoutId: number }

// What the editor holds and the server doesn't know yet.
export interface EditorDraftContent {
  heading: WorkoutHeadingDraft
  endTime: string
  exercises: WorkoutExerciseDraft[]
}

export interface StoredEditorDraft extends EditorDraftContent {
  version: typeof STORAGE_VERSION
  ownerId: number
  // ISO instant of the last write, for the "Restored … from 09.42" notice.
  savedAt: string
  // Set by an expired-token sign-out: keep this copy for the same user's next
  // sign-in, even though there is no token right now.
  heldForSignIn: boolean
  // A new page whose heading POST already succeeded: the retry must update that
  // workout, not create a second one, even after a reload.
  savedWorkoutId: number | null
}

// The subset of Storage used here, so the tests can pass a Map-backed fake.
export type DraftStore = Pick<
  Storage,
  'getItem' | 'setItem' | 'removeItem' | 'key' | 'length'
>

// sessionStorage, or null when this browser won't give it to us. Even reading
// the property can throw when site data is blocked.
function browserStore(): DraftStore | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage
  } catch {
    return null
  }
}

export function editorDraftKey(route: EditorDraftRoute): string {
  return route.kind === 'new'
    ? `${KEY_PREFIX}new`
    : `${KEY_PREFIX}workout.${route.workoutId}`
}

// — reading back —
// What comes out of storage is untrusted: an older build, a manual edit or a
// half-written value. Each check below is what the editor relies on later;
// anything that fails one is treated as no draft at all.

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function hasStrings(value: Record<string, unknown>, keys: string[]): boolean {
  return keys.every((key) => typeof value[key] === 'string')
}

function isHeading(value: unknown): value is WorkoutHeadingDraft {
  return (
    isRecord(value) &&
    hasStrings(value, [
      'date',
      'startTime',
      'title',
      'bodyweightKg',
      'location',
      'notes',
    ])
  )
}

function isSet(value: unknown): value is WorkoutSetDraft {
  return (
    isRecord(value) &&
    hasStrings(value, ['clientId', 'weight', 'reps']) &&
    typeof value.isWarmup === 'boolean'
  )
}

// The block's "last time" hint. Absent in drafts stored before it existed, and
// without isWarmup in drafts stored before that flag existed; such a hint just
// reads as a working set, as it did when it was stored.
function isLastSet(value: unknown): boolean {
  return (
    value === undefined ||
    value === null ||
    (isRecord(value) &&
      (value.weight === null || typeof value.weight === 'number') &&
      Number.isSafeInteger(value.reps) &&
      (value.isWarmup === undefined || typeof value.isWarmup === 'boolean'))
  )
}

function isExercise(value: unknown): value is WorkoutExerciseDraft {
  return (
    isRecord(value) &&
    hasStrings(value, ['clientId', 'exerciseName']) &&
    isLastSet(value.lastSet) &&
    (value.exerciseId === null || Number.isSafeInteger(value.exerciseId)) &&
    typeof value.isBodyweight === 'boolean' &&
    typeof value.isAddedWeightEnabled === 'boolean' &&
    Array.isArray(value.sets) &&
    value.sets.every(isSet)
  )
}

function isStoredDraft(value: unknown): value is StoredEditorDraft {
  return (
    isRecord(value) &&
    value.version === STORAGE_VERSION &&
    Number.isSafeInteger(value.ownerId) &&
    typeof value.savedAt === 'string' &&
    !Number.isNaN(Date.parse(value.savedAt)) &&
    typeof value.heldForSignIn === 'boolean' &&
    (value.savedWorkoutId === null ||
      Number.isSafeInteger(value.savedWorkoutId)) &&
    typeof value.endTime === 'string' &&
    isHeading(value.heading) &&
    Array.isArray(value.exercises) &&
    value.exercises.every(isExercise)
  )
}

// Parses one stored value. Null for anything absent, malformed or from another
// version; ownership is the caller's question.
export function parseStoredEditorDraft(
  raw: string | null,
): StoredEditorDraft | null {
  if (raw === null) {
    return null
  }
  try {
    const value: unknown = JSON.parse(raw)
    return isStoredDraft(value) ? value : null
  } catch {
    return null
  }
}

// — comparing —

// The draft's content without its React keys. Two drafts with the same
// fingerprint would save the same page, which is what "has anything changed?"
// means: the edit route's server copy gets fresh client ids on every load.
export function draftFingerprint(content: EditorDraftContent): string {
  return JSON.stringify({
    heading: content.heading,
    endTime: content.endTime,
    exercises: content.exercises.map((exercise) => ({
      exerciseId: exercise.exerciseId,
      exerciseName: exercise.exerciseName,
      isBodyweight: exercise.isBodyweight,
      isAddedWeightEnabled: exercise.isAddedWeightEnabled,
      sets: exercise.sets.map((set) => ({
        weight: set.weight,
        reps: set.reps,
        isWarmup: set.isWarmup,
      })),
    })),
  })
}

export function countDraftSets(exercises: WorkoutExerciseDraft[]): number {
  return exercises.reduce((total, exercise) => total + exercise.sets.length, 0)
}

// The question Cancel asks when the draft has changed. It names what goes
// (PRODUCT.md: destructive actions name their consequences), and on a new
// page whose heading already reached the server it says that part stays.
export function describeDiscard(options: {
  isEditing: boolean
  setCount: number
  partlySaved: boolean
}): string {
  if (options.isEditing) {
    return 'Discard your changes? The page stays as it was last saved.'
  }
  const question =
    options.setCount === 0
      ? 'Discard this page?'
      : `Discard this page and its ${formatCount(options.setCount, 'set')}?`
  return options.partlySaved
    ? `${question} The part already saved stays in your notebook.`
    : question
}

// When the restored copy was last written: just the time on the same local
// day, the date too otherwise, so a page left open since last week says so.
export function describeDraftSavedAt(savedAt: string, now: Date): string {
  const saved = new Date(savedAt)
  const time = formatWorkoutTime(savedAt)
  const sameDay =
    saved.getFullYear() === now.getFullYear() &&
    saved.getMonth() === now.getMonth() &&
    saved.getDate() === now.getDate()
  if (sameDay) {
    return time
  }
  const month = String(saved.getMonth() + 1).padStart(2, '0')
  const day = String(saved.getDate()).padStart(2, '0')
  return `${formatWorkoutLongDate(`${saved.getFullYear()}-${month}-${day}`)}, ${time}`
}

// — storage —

// Every stored draft's key. Collected before any removal, because removing
// while walking the index shifts it.
function draftKeys(store: DraftStore): string[] {
  const keys: string[] = []
  for (let index = 0; index < store.length; index += 1) {
    const key = store.key(index)
    if (key?.startsWith(KEY_PREFIX)) {
      keys.push(key)
    }
  }
  return keys
}

// Runs a storage operation, swallowing what storage itself throws (quota,
// blocked site data). A failed write only means the draft can't survive a
// reload; it must never break the editor.
function withStore(
  store: DraftStore | null,
  operation: (store: DraftStore) => void,
): void {
  if (store === null) {
    return
  }
  try {
    operation(store)
  } catch {
    // Storage unavailable or full: carry on without the copy.
  }
}

// The copy for this route, if it belongs to `ownerId`. Another account's copy
// is removed on the way, never returned.
export function loadEditorDraft(
  route: EditorDraftRoute,
  ownerId: number,
  store: DraftStore | null = browserStore(),
): StoredEditorDraft | null {
  let draft: StoredEditorDraft | null = null
  withStore(store, (available) => {
    const key = editorDraftKey(route)
    const parsed = parseStoredEditorDraft(available.getItem(key))
    if (parsed !== null && parsed.ownerId === ownerId) {
      draft = parsed
    } else if (available.getItem(key) !== null) {
      available.removeItem(key)
    }
  })
  return draft
}

export function saveEditorDraft(
  route: EditorDraftRoute,
  draft: Omit<StoredEditorDraft, 'version' | 'heldForSignIn' | 'savedAt'>,
  now: Date = new Date(),
  store: DraftStore | null = browserStore(),
): void {
  const stored: StoredEditorDraft = {
    version: STORAGE_VERSION,
    heldForSignIn: false,
    savedAt: now.toISOString(),
    ...draft,
  }
  withStore(store, (available) =>
    available.setItem(editorDraftKey(route), JSON.stringify(stored)),
  )
}

export function removeEditorDraft(
  route: EditorDraftRoute,
  store: DraftStore | null = browserStore(),
): void {
  withStore(store, (available) => available.removeItem(editorDraftKey(route)))
}

// Rewrites every stored draft through `change`; null removes it. Unreadable
// values are removed too: nothing here can tell whose they are.
function updateEditorDrafts(
  store: DraftStore | null,
  change: (draft: StoredEditorDraft) => StoredEditorDraft | null,
): void {
  withStore(store, (available) => {
    for (const key of draftKeys(available)) {
      const parsed = parseStoredEditorDraft(available.getItem(key))
      const next = parsed === null ? null : change(parsed)
      if (next === null) {
        available.removeItem(key)
      } else {
        available.setItem(key, JSON.stringify(next))
      }
    }
  })
}

// Sign-out, account deletion and any session ending that isn't a plain
// expiry. `keepHeld` is for startup with no token: a draft held by this tab's
// expiry sign-out survives it; everything else goes.
export function clearEditorDrafts(
  options: { keepHeld?: boolean } = {},
  store: DraftStore | null = browserStore(),
): void {
  updateEditorDrafts(store, (draft) =>
    options.keepHeld && draft.heldForSignIn ? draft : null,
  )
}

// The token expired: keep every draft for the same user's next sign-in.
export function holdEditorDraftsForSignIn(
  store: DraftStore | null = browserStore(),
): void {
  updateEditorDrafts(store, (draft) => ({ ...draft, heldForSignIn: true }))
}

// Someone is signed in as `ownerId`: their drafts are ordinary again, and any
// other account's are removed.
export function adoptEditorDrafts(
  ownerId: number,
  store: DraftStore | null = browserStore(),
): void {
  updateEditorDrafts(store, (draft) =>
    draft.ownerId === ownerId ? { ...draft, heldForSignIn: false } : null,
  )
}

// Consent for optional details was withdrawn in this tab: the four fields
// leave every stored draft too, as they leave the server (specs/001 FR-032).
export function stripEditorDraftDetails(
  store: DraftStore | null = browserStore(),
): void {
  updateEditorDrafts(store, (draft) => ({
    ...draft,
    heading: dropOptionalDetails(draft.heading),
  }))
}
