import { describe, expect, it } from 'vitest'
import {
  adoptEditorDrafts,
  clearEditorDrafts,
  countDraftSets,
  describeDiscard,
  describeDraftSavedAt,
  draftFingerprint,
  editorDraftKey,
  holdEditorDraftsForSignIn,
  loadEditorDraft,
  parseStoredEditorDraft,
  removeEditorDraft,
  saveEditorDraft,
  stripEditorDraftDetails,
  type DraftStore,
  type EditorDraftContent,
  type EditorDraftRoute,
} from './editorDraftStorage'
import {
  addSetToExercise,
  createInitialHeadingDraft,
  createWorkoutExerciseDraft,
} from './newWorkoutDraft'

// Vitest runs in Node, which has no sessionStorage. The module takes the store
// as a parameter, so a Map behind the five methods it uses is enough.
function fakeStore(): DraftStore & { entries: Map<string, string> } {
  const entries = new Map<string, string>()
  return {
    entries,
    get length() {
      return entries.size
    },
    key: (index) => [...entries.keys()][index] ?? null,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => void entries.set(key, value),
    removeItem: (key) => void entries.delete(key),
  }
}

// Storage that refuses everything, like Safari with site data blocked.
function throwingStore(): DraftStore {
  const refuse = () => {
    throw new Error('SecurityError')
  }
  return {
    get length(): number {
      return refuse()
    },
    key: refuse,
    getItem: refuse,
    setItem: refuse,
    removeItem: refuse,
  }
}

const newPage: EditorDraftRoute = { kind: 'new' }
const editPage: EditorDraftRoute = { kind: 'edit', workoutId: 12 }
const savedAt = new Date(2026, 9, 4, 9, 42)

function sampleContent(): EditorDraftContent {
  const squat = addSetToExercise(
    createWorkoutExerciseDraft('block-1', 'set-1', 42, 'Back Squat', false),
    'set-2',
  )
  return {
    heading: {
      ...createInitialHeadingDraft(new Date(2026, 9, 4, 9, 30)),
      title: 'Legs',
      bodyweightKg: '81.4',
      location: 'Home gym',
      notes: 'Knee fine',
    },
    endTime: '',
    exercises: [
      {
        ...squat,
        sets: squat.sets.map((set) => ({ ...set, weight: '100', reps: '5' })),
      },
    ],
  }
}

function saveSample(
  store: DraftStore,
  route: EditorDraftRoute = newPage,
  ownerId = 7,
) {
  saveEditorDraft(
    route,
    { ownerId, savedWorkoutId: null, ...sampleContent() },
    savedAt,
    store,
  )
}

describe('saveEditorDraft and loadEditorDraft', () => {
  it('round-trips the draft for its owner', () => {
    const store = fakeStore()

    saveSample(store)
    const loaded = loadEditorDraft(newPage, 7, store)

    expect(loaded).toMatchObject({
      ownerId: 7,
      savedAt: savedAt.toISOString(),
      heldForSignIn: false,
      savedWorkoutId: null,
      ...sampleContent(),
    })
  })

  it('keeps one draft per route', () => {
    const store = fakeStore()

    saveSample(store, newPage)
    saveSample(store, editPage)

    expect([...store.entries.keys()].sort()).toEqual([
      editorDraftKey(newPage),
      editorDraftKey(editPage),
    ])
  })

  // A reload must not turn a retry into a second workout.
  it('keeps the id of a new page whose heading was already saved', () => {
    const store = fakeStore()

    saveEditorDraft(
      newPage,
      { ownerId: 7, savedWorkoutId: 55, ...sampleContent() },
      savedAt,
      store,
    )

    expect(loadEditorDraft(newPage, 7, store)?.savedWorkoutId).toBe(55)
  })

  it("never returns another account's draft, and removes it", () => {
    const store = fakeStore()
    saveSample(store, newPage, 7)

    expect(loadEditorDraft(newPage, 8, store)).toBeNull()
    expect(store.entries.size).toBe(0)
  })

  it('treats a corrupt value as no draft, and removes it', () => {
    const store = fakeStore()
    store.entries.set(editorDraftKey(newPage), '{"version":1,"heading":')

    expect(loadEditorDraft(newPage, 7, store)).toBeNull()
    expect(store.entries.size).toBe(0)
  })

  it('returns null when there is no draft', () => {
    expect(loadEditorDraft(newPage, 7, fakeStore())).toBeNull()
  })

  // Private mode or blocked site data: the editor carries on without a copy.
  it('survives storage that throws or is missing', () => {
    expect(() => saveSample(throwingStore())).not.toThrow()
    expect(loadEditorDraft(newPage, 7, throwingStore())).toBeNull()
    expect(loadEditorDraft(newPage, 7, null)).toBeNull()
    expect(() => clearEditorDrafts({}, throwingStore())).not.toThrow()
  })

  it('removes only the given route', () => {
    const store = fakeStore()
    saveSample(store, newPage)
    saveSample(store, editPage)

    removeEditorDraft(newPage, store)

    expect([...store.entries.keys()]).toEqual([editorDraftKey(editPage)])
  })
})

describe('parseStoredEditorDraft', () => {
  function storedWith(changes: Record<string, unknown>): string {
    const store = fakeStore()
    saveSample(store)
    const stored = JSON.parse(
      store.entries.get(editorDraftKey(newPage)) ?? '',
    ) as Record<string, unknown>
    return JSON.stringify({ ...stored, ...changes })
  }

  it('rejects another storage version', () => {
    expect(parseStoredEditorDraft(storedWith({ version: 2 }))).toBeNull()
  })

  it('rejects a set with a numeric weight', () => {
    const content = sampleContent()
    const exercises = [
      {
        ...content.exercises[0],
        sets: [{ clientId: 's', weight: 100, reps: '5', isWarmup: false }],
      },
    ]

    expect(parseStoredEditorDraft(storedWith({ exercises }))).toBeNull()
  })

  // Drafts stored before blocks carried a "last time" set still restore.
  it('accepts a block with or without a last set', () => {
    const block = sampleContent().exercises[0]
    const withoutLastSet: Record<string, unknown> = { ...block }
    delete withoutLastSet.lastSet

    expect(
      parseStoredEditorDraft(storedWith({ exercises: [withoutLastSet] })),
    ).not.toBeNull()
    expect(
      parseStoredEditorDraft(
        storedWith({
          exercises: [{ ...block, lastSet: { weight: null, reps: 12 } }],
        }),
      )?.exercises[0].lastSet,
    ).toEqual({ weight: null, reps: 12 })
  })

  it('rejects a malformed last set', () => {
    const block = sampleContent().exercises[0]

    expect(
      parseStoredEditorDraft(
        storedWith({ exercises: [{ ...block, lastSet: { reps: '5' } }] }),
      ),
    ).toBeNull()
  })

  it('rejects a heading with a missing field', () => {
    const heading: Record<string, unknown> = { ...sampleContent().heading }
    delete heading.notes

    expect(parseStoredEditorDraft(storedWith({ heading }))).toBeNull()
  })

  it('rejects an unreadable timestamp or owner', () => {
    expect(parseStoredEditorDraft(storedWith({ savedAt: 'later' }))).toBeNull()
    expect(parseStoredEditorDraft(storedWith({ ownerId: '7' }))).toBeNull()
  })

  it('rejects non-JSON and null', () => {
    expect(parseStoredEditorDraft('not json')).toBeNull()
    expect(parseStoredEditorDraft(null)).toBeNull()
  })
})

describe('session ending', () => {
  it('clears every draft on sign-out', () => {
    const store = fakeStore()
    saveSample(store, newPage)
    saveSample(store, editPage)

    clearEditorDrafts({}, store)

    expect(store.entries.size).toBe(0)
  })

  // The token expired mid-session: the draft waits for the same user.
  it('holds drafts through an expiry, and startup without a token keeps them', () => {
    const store = fakeStore()
    saveSample(store)

    holdEditorDraftsForSignIn(store)
    clearEditorDrafts({ keepHeld: true }, store)

    expect(loadEditorDraft(newPage, 7, store)?.heldForSignIn).toBe(true)
  })

  // e.g. another tab signed out while this one was discarded.
  it('removes drafts that were not held when starting without a token', () => {
    const store = fakeStore()
    saveSample(store)

    clearEditorDrafts({ keepHeld: true }, store)

    expect(store.entries.size).toBe(0)
  })

  it('a deliberate sign-out clears even a held draft', () => {
    const store = fakeStore()
    saveSample(store)
    holdEditorDraftsForSignIn(store)

    clearEditorDrafts({}, store)

    expect(store.entries.size).toBe(0)
  })

  it("signing in keeps the user's drafts, released from hold, and removes others", () => {
    const store = fakeStore()
    saveSample(store, newPage, 7)
    saveSample(store, editPage, 8)
    holdEditorDraftsForSignIn(store)

    adoptEditorDrafts(7, store)

    expect(loadEditorDraft(newPage, 7, store)?.heldForSignIn).toBe(false)
    expect(store.entries.has(editorDraftKey(editPage))).toBe(false)
  })

  it('leaves other keys in storage alone', () => {
    const store = fakeStore()
    store.entries.set('unrelated', 'x')
    saveSample(store)

    clearEditorDrafts({}, store)

    expect([...store.entries.keys()]).toEqual(['unrelated'])
  })
})

describe('stripEditorDraftDetails', () => {
  // Consent withdrawn: the four optional details leave stored drafts too.
  it('removes title, bodyweight, gym and notes and keeps the rest', () => {
    const store = fakeStore()
    saveSample(store)

    stripEditorDraftDetails(store)
    const loaded = loadEditorDraft(newPage, 7, store)

    expect(loaded?.heading).toMatchObject({
      title: '',
      bodyweightKg: '',
      location: '',
      notes: '',
      date: '2026-10-04',
      startTime: '09:30',
    })
    expect(loaded?.exercises).toEqual(sampleContent().exercises)
  })
})

describe('draftFingerprint', () => {
  // The edit route's server copy gets fresh client ids on every load.
  it('ignores client ids', () => {
    const content = sampleContent()
    const reKeyed: EditorDraftContent = {
      ...content,
      exercises: content.exercises.map((exercise) => ({
        ...exercise,
        clientId: 'other-block',
        sets: exercise.sets.map((set, index) => ({
          ...set,
          clientId: `other-${index}`,
        })),
      })),
    }

    expect(draftFingerprint(reKeyed)).toBe(draftFingerprint(content))
  })

  it('changes when a set value changes', () => {
    const content = sampleContent()
    const changed: EditorDraftContent = {
      ...content,
      exercises: [
        {
          ...content.exercises[0],
          sets: content.exercises[0].sets.map((set) => ({
            ...set,
            reps: '6',
          })),
        },
      ],
    }

    expect(draftFingerprint(changed)).not.toBe(draftFingerprint(content))
  })
})

describe('countDraftSets', () => {
  it('counts sets across blocks', () => {
    const content = sampleContent()

    expect(countDraftSets([...content.exercises, ...content.exercises])).toBe(4)
    expect(countDraftSets([])).toBe(0)
  })
})

describe('describeDiscard', () => {
  it('names the sets a new page would lose', () => {
    expect(
      describeDiscard({ isEditing: false, setCount: 11, partlySaved: false }),
    ).toBe('Discard this page and its 11 sets?')
    expect(
      describeDiscard({ isEditing: false, setCount: 1, partlySaved: false }),
    ).toBe('Discard this page and its 1 set?')
    expect(
      describeDiscard({ isEditing: false, setCount: 0, partlySaved: false }),
    ).toBe('Discard this page?')
  })

  it('says what stays when the heading already reached the server', () => {
    expect(
      describeDiscard({ isEditing: false, setCount: 3, partlySaved: true }),
    ).toBe(
      'Discard this page and its 3 sets? The part already saved stays in your notebook.',
    )
  })

  it('says an edited page keeps its last saved version', () => {
    expect(
      describeDiscard({ isEditing: true, setCount: 3, partlySaved: false }),
    ).toBe('Discard your changes? The page stays as it was last saved.')
  })
})

describe('describeDraftSavedAt', () => {
  it('gives just the time on the same day', () => {
    expect(
      describeDraftSavedAt(savedAt.toISOString(), new Date(2026, 9, 4, 11)),
    ).toBe('09.42')
  })

  it('adds the date on another day', () => {
    expect(
      describeDraftSavedAt(savedAt.toISOString(), new Date(2026, 9, 6, 8)),
    ).toBe('4 October 2026, 09.42')
  })
})
