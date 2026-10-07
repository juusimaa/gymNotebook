import { describe, expect, it } from 'vitest'
import {
  addSetToExercise,
  createEmptySetDraft,
  createExistingWorkoutDraft,
  createInitialHeadingDraft,
  createLocalEndedAt,
  createWorkoutExerciseDraft,
  describeDraftSet,
  describeHeadingWhen,
  dropOptionalDetails,
  hasOptionalDetails,
  needsExplicitFinishTime,
  prepareWorkoutDraft,
  removeSetFromExercise,
  restoreSetToExercise,
  updateSetInExercise,
  withLastTime,
  type WorkoutExerciseDraft,
} from './newWorkoutDraft'

function createTwoSetExercise() {
  const exercise = createWorkoutExerciseDraft(
    'block-1',
    'set-1',
    42,
    'Back Squat',
    false,
  )

  return addSetToExercise(exercise, 'set-2')
}

describe('createInitialHeadingDraft', () => {
  // The draft represents the user's local calendar values, so constructing a
  // local Date should preserve those values rather than convert them through UTC.
  it('uses the local date and time with two-digit padding', () => {
    const now = new Date(2026, 8, 15, 7, 5)

    const draft = createInitialHeadingDraft(now)

    expect(draft.date).toBe('2026-09-15')
    expect(draft.startTime).toBe('07:05')
  })

  it('initializes the optional heading fields as empty strings', () => {
    const draft = createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5))

    expect(draft).toMatchObject({
      title: '',
      bodyweightKg: '',
      location: '',
      notes: '',
    })
  })
})

describe('createExistingWorkoutDraft', () => {
  it('rehydrates heading, finish time, and ordered exercise sets', () => {
    let nextId = 0

    const draft = createExistingWorkoutDraft(
      {
        id: 12,
        date: '2026-09-15',
        startedAt: new Date(2026, 8, 15, 23, 30).toISOString(),
        endedAt: new Date(2026, 8, 16, 0, 15).toISOString(),
        title: 'Late pull',
        bodyweightKg: 78.4,
        location: 'Liikuntamylly',
        notes: null,
        exercises: [
          {
            id: 20,
            exerciseId: 42,
            exerciseName: 'Pull-up',
            isBodyweight: true,
            sets: [
              {
                id: 21,
                setNumber: 1,
                reps: 5,
                weight: 10,
                isWarmup: false,
              },
            ],
          },
        ],
      },
      () => `client-${(nextId += 1)}`,
    )

    expect(draft).toEqual({
      heading: {
        date: '2026-09-15',
        startTime: '23:30',
        title: 'Late pull',
        bodyweightKg: '78.4',
        location: 'Liikuntamylly',
        notes: '',
      },
      endTime: '00:15',
      exercises: [
        {
          clientId: 'client-1',
          exerciseId: 42,
          exerciseName: 'Pull-up',
          isBodyweight: true,
          isAddedWeightEnabled: true,
          sets: [
            {
              clientId: 'client-2',
              weight: '10',
              reps: '5',
              isWarmup: false,
            },
          ],
        },
      ],
    })
  })
})

describe('createLocalEndedAt', () => {
  it('rolls an earlier finish time onto the next local day', () => {
    expect(createLocalEndedAt('2026-09-15', '23:30', '00:15')).toBe(
      new Date(2026, 8, 16, 0, 15).toISOString(),
    )
  })
})

describe('needsExplicitFinishTime', () => {
  const heading = { date: '2026-09-30', startTime: '07:15' }

  it.each([
    ['at the start', new Date(2026, 8, 30, 7, 15)],
    ['a normal session later', new Date(2026, 8, 30, 8, 40)],
    ['exactly six hours later', new Date(2026, 8, 30, 13, 15)],
  ])('lets a finish %s mean now', (_, now) => {
    expect(needsExplicitFinishTime(heading, now)).toBe(false)
  })

  it.each([
    ['more than six hours later', new Date(2026, 8, 30, 13, 16)],
    ['on a later day', new Date(2026, 9, 7, 12, 6)],
    ['before the page started', new Date(2026, 8, 30, 7, 14)],
  ])('asks for the time when finishing %s', (_, now) => {
    expect(needsExplicitFinishTime(heading, now)).toBe(true)
  })

  it('crosses midnight within the window without asking', () => {
    expect(
      needsExplicitFinishTime(
        { date: '2026-09-15', startTime: '23:30' },
        new Date(2026, 8, 16, 0, 15),
      ),
    ).toBe(false)
  })

  it('leaves an unparseable start to the save validation', () => {
    expect(
      needsExplicitFinishTime(
        { date: '2026-09-30', startTime: '' },
        new Date(2026, 9, 7),
      ),
    ).toBe(false)
  })
})

describe('createEmptySetDraft', () => {
  it('returns an empty editable set with the supplied client id', () => {
    const draft = createEmptySetDraft('set-1')

    expect(draft).toEqual({
      clientId: 'set-1',
      weight: '',
      reps: '',
      isWarmup: false,
    })
  })
})

describe('createWorkoutExerciseDraft', () => {
  it('initializes an existing loaded exercise with one empty set', () => {
    const draft = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      42,
      'Back Squat',
      false,
    )

    expect(draft).toEqual({
      clientId: 'block-1',
      exerciseId: 42,
      exerciseName: 'Back Squat',
      isBodyweight: false,
      isAddedWeightEnabled: false,
      lastSet: null,
      sets: [
        {
          clientId: 'set-1',
          weight: '',
          reps: '',
          isWarmup: false,
        },
      ],
    })
  })

  it('preserves a null id and bodyweight flag for a new exercise', () => {
    const draft = createWorkoutExerciseDraft(
      'block-2',
      'set-2',
      null,
      'Pull-up',
      true,
    )

    expect(draft).toEqual({
      clientId: 'block-2',
      exerciseId: null,
      exerciseName: 'Pull-up',
      isBodyweight: true,
      isAddedWeightEnabled: false,
      lastSet: null,
      sets: [
        {
          clientId: 'set-2',
          weight: '',
          reps: '',
          isWarmup: false,
        },
      ],
    })
  })

  // Picked from autocomplete, the block keeps the exercise's previous set
  // for its "last time" line.
  it('keeps the last set it was picked with', () => {
    const draft = createWorkoutExerciseDraft(
      'b',
      's',
      42,
      'Back Squat',
      false,
      {
        weight: 100,
        reps: 5,
        isWarmup: false,
      },
    )

    expect(draft.lastSet).toEqual({ weight: 100, reps: 5, isWarmup: false })
  })

  // The ditto mark across sessions: set 1 is last time's set, written in.
  it('writes last time into the first set', () => {
    const draft = createWorkoutExerciseDraft(
      'b',
      's',
      42,
      'Back Squat',
      false,
      {
        weight: 92.5,
        reps: 5,
        isWarmup: false,
      },
    )

    expect(draft.sets).toEqual([
      { clientId: 's', weight: '92.5', reps: '5', isWarmup: false },
    ])
    expect(draft.isAddedWeightEnabled).toBe(false)
  })

  it('copies a warm-up last time as a warm-up', () => {
    const draft = createWorkoutExerciseDraft(
      'b',
      's',
      42,
      'Overhead Press',
      false,
      {
        weight: 20,
        reps: 10,
        isWarmup: true,
      },
    )

    expect(draft.sets[0]).toMatchObject({
      weight: '20',
      reps: '10',
      isWarmup: true,
    })
  })

  it('leaves weight empty for an unloaded bodyweight last time', () => {
    const draft = createWorkoutExerciseDraft('b', 's', 7, 'Pull-up', true, {
      weight: null,
      reps: 8,
      isWarmup: false,
    })

    expect(draft.sets[0]).toMatchObject({ weight: '', reps: '8' })
    expect(draft.isAddedWeightEnabled).toBe(false)
  })

  // Otherwise the copied added weight would sit in a hidden field.
  it('shows the weight field for a bodyweight last time with added weight', () => {
    const draft = createWorkoutExerciseDraft('b', 's', 7, 'Pull-up', true, {
      weight: 10,
      reps: 6,
      isWarmup: false,
    })

    expect(draft.sets[0]).toMatchObject({ weight: '10', reps: '6' })
    expect(draft.isAddedWeightEnabled).toBe(true)
  })

  // Last time opened with a warm-up, so this time does too: the warm-up's own
  // figures, not the working weight flagged as one.
  it('starts from the warm-up last time opened with', () => {
    const draft = createWorkoutExerciseDraft(
      'b',
      's',
      42,
      'Back Squat',
      false,
      { weight: 100, reps: 5, isWarmup: false },
      { weight: 60, reps: 10, isWarmup: true },
    )

    expect(draft.sets).toEqual([
      { clientId: 's', weight: '60', reps: '10', isWarmup: true },
    ])
    // The "last time" line still names the working set.
    expect(draft.lastSet).toEqual({ weight: 100, reps: 5, isWarmup: false })
  })

  it('starts from the last working set when last time opened with one', () => {
    const draft = createWorkoutExerciseDraft(
      'b',
      's',
      42,
      'Back Squat',
      false,
      { weight: 100, reps: 5, isWarmup: false },
      { weight: 95, reps: 5, isWarmup: false },
    )

    expect(draft.sets).toEqual([
      { clientId: 's', weight: '100', reps: '5', isWarmup: false },
    ])
  })

  it('shows the weight field for an opening warm-up with added weight', () => {
    const draft = createWorkoutExerciseDraft(
      'b',
      's',
      7,
      'Dip',
      true,
      { weight: null, reps: 12, isWarmup: false },
      { weight: 5, reps: 8, isWarmup: true },
    )

    expect(draft.sets[0]).toMatchObject({ weight: '5', reps: '8' })
    expect(draft.isAddedWeightEnabled).toBe(true)
  })
})

describe('set draft operations', () => {
  it('starts an empty block with an empty set', () => {
    const empty = {
      ...createWorkoutExerciseDraft('b', 's', 42, 'Back Squat', false),
      sets: [],
    }

    expect(addSetToExercise(empty, 'set-1').sets).toEqual([
      { clientId: 'set-1', weight: '', reps: '', isWarmup: false },
    ])
  })

  // The ditto mark: the new set repeats the figures above, as a working set.
  it('repeats the previous set as a working set with a new client id', () => {
    const block = createTwoSetExercise()
    const filled = updateSetInExercise(block, 'set-2', {
      weight: '60',
      reps: '5',
      isWarmup: true,
    })

    const result = addSetToExercise(filled, 'set-3')

    expect(result.sets[2]).toEqual({
      clientId: 'set-3',
      weight: '60',
      reps: '5',
      isWarmup: false,
    })
    expect(result.sets[1]).toBe(filled.sets[1])
    expect(filled.sets).toHaveLength(2)
  })

  it('restores a removed set at its old position', () => {
    const block = createTwoSetExercise()
    const removed = block.sets[0]
    const without = removeSetFromExercise(block, 'set-1')

    const result = restoreSetToExercise(without, removed, 0)

    expect(result.sets.map((set) => set.clientId)).toEqual(['set-1', 'set-2'])
    expect(without.sets).toHaveLength(1)
  })

  it('restores at the end when the block has since shrunk', () => {
    const block = createTwoSetExercise()
    const removed = { ...block.sets[1], clientId: 'set-9' }

    const result = restoreSetToExercise(block, removed, 7)

    expect(result.sets.map((set) => set.clientId)).toEqual([
      'set-1',
      'set-2',
      'set-9',
    ])
  })

  it('adds an empty set after an empty set without changing the original exercise', () => {
    const original = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      42,
      'Back Squat',
      false,
    )

    const result = addSetToExercise(original, 'set-2')

    expect(result).not.toBe(original)
    expect(result.sets).not.toBe(original.sets)
    expect(result.sets).toEqual([
      {
        clientId: 'set-1',
        weight: '',
        reps: '',
        isWarmup: false,
      },
      {
        clientId: 'set-2',
        weight: '',
        reps: '',
        isWarmup: false,
      },
    ])
    expect(original.sets).toHaveLength(1)
  })

  it('updates only the targeted set without changing the original exercise', () => {
    const original = createTwoSetExercise()

    const result = updateSetInExercise(original, 'set-2', {
      weight: '100',
      reps: '5',
      isWarmup: true,
    })

    expect(result).not.toBe(original)
    expect(result.sets).not.toBe(original.sets)
    expect(result.sets).toEqual([
      {
        clientId: 'set-1',
        weight: '',
        reps: '',
        isWarmup: false,
      },
      {
        clientId: 'set-2',
        weight: '100',
        reps: '5',
        isWarmup: true,
      },
    ])
    expect(original.sets[1]).toEqual({
      clientId: 'set-2',
      weight: '',
      reps: '',
      isWarmup: false,
    })
  })

  it('removes only the targeted set without changing the original exercise', () => {
    const original = createTwoSetExercise()

    const result = removeSetFromExercise(original, 'set-1')

    expect(result).not.toBe(original)
    expect(result.sets).not.toBe(original.sets)
    expect(result.sets).toEqual([
      {
        clientId: 'set-2',
        weight: '',
        reps: '',
        isWarmup: false,
      },
    ])
    expect(original.sets.map((set) => set.clientId)).toEqual(['set-1', 'set-2'])
  })
})

describe('prepareWorkoutDraft', () => {
  it('converts local heading values and loaded sets to API requests', () => {
    const heading = createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5))
    heading.title = ' Leg day '
    heading.bodyweightKg = '78,4'
    heading.location = ' Liikuntamylly '

    const exercise = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      42,
      'Back Squat',
      false,
    )
    exercise.sets[0].weight = '90'
    exercise.sets[0].reps = '3'

    const result = prepareWorkoutDraft(heading, [exercise])

    expect(result).toEqual({
      ok: true,
      value: {
        workout: {
          date: '2026-09-15',
          startedAt: new Date(2026, 8, 15, 7, 5).toISOString(),
          title: 'Leg day',
          bodyweightKg: 78.4,
          location: 'Liikuntamylly',
          notes: null,
        },
        exercises: {
          exercises: [
            {
              exerciseName: 'Back Squat',
              sets: [{ weight: 90, reps: 3, isWarmup: false }],
            },
          ],
        },
      },
    })
  })

  it('sends null weight for bodyweight sets without added load', () => {
    const heading = createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5))
    const exercise = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      null,
      'Pull-up',
      true,
    )
    exercise.sets[0].reps = '8'

    const result = prepareWorkoutDraft(heading, [exercise])

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.value.exercises.exercises[0].sets[0].weight).toBeNull()
    }
  })

  it('accepts added weight for a bodyweight exercise when enabled', () => {
    const heading = createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5))
    const exercise = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      null,
      'Pull-up',
      true,
    )
    exercise.isAddedWeightEnabled = true
    exercise.sets[0].weight = '10'
    exercise.sets[0].reps = '5'

    const result = prepareWorkoutDraft(heading, [exercise])

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.value.exercises.exercises[0].sets[0].weight).toBe(10)
    }
  })

  it.each([
    ['no exercises', [], 'Add at least one exercise.', { area: 'exercises' }],
    [
      'missing reps',
      [{ weight: '100', reps: '' }],
      'Back Squat, set 1: reps must be a whole number greater than zero.',
      { area: 'set', exerciseIndex: 0, setIndex: 0, field: 'reps' },
    ],
    [
      'missing loaded weight',
      [{ weight: '', reps: '5' }],
      'Back Squat, set 1: enter a valid weight.',
      { area: 'set', exerciseIndex: 0, setIndex: 0, field: 'weight' },
    ],
  ])('rejects %s', (_scenario, setValues, expectedMessage, expectedAt) => {
    const heading = createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5))
    const exercises = setValues.map(({ weight, reps }) => {
      const exercise = createWorkoutExerciseDraft(
        'block-1',
        'set-1',
        42,
        'Back Squat',
        false,
      )
      exercise.sets[0].weight = weight
      exercise.sets[0].reps = reps
      return exercise
    })

    expect(prepareWorkoutDraft(heading, exercises)).toEqual({
      ok: false,
      at: expectedAt,
      message: expectedMessage,
    })
  })

  // The editor opens its collapsed heading for these, so the message never
  // points at a field the user can't see.
  it.each([
    [
      'an empty start time',
      { startTime: '' },
      'Enter a valid date and start time.',
      'startTime',
    ],
    [
      'an empty date',
      { date: '' },
      'Enter a valid date and start time.',
      'date',
    ],
    [
      'a zero bodyweight',
      { bodyweightKg: '0' },
      'Bodyweight must be a number greater than zero.',
      'bodyweightKg',
    ],
  ])(
    'points %s at its heading field',
    (_scenario, changes, expectedMessage, field) => {
      const heading = {
        ...createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5)),
        ...changes,
      }

      expect(prepareWorkoutDraft(heading, [])).toEqual({
        ok: false,
        at: { area: 'heading', field },
        message: expectedMessage,
      })
    },
  )

  it('points at the set that fails, not the first set', () => {
    const block = updateSetInExercise(createTwoSetExercise(), 'set-1', {
      weight: '100',
      reps: '5',
    })
    const second = updateSetInExercise(block, 'set-2', {
      weight: '100',
      reps: '0',
    })
    const heading = createInitialHeadingDraft(new Date(2026, 8, 15, 7, 5))

    const result = prepareWorkoutDraft(heading, [second])

    expect(result.ok === false && result.at).toEqual({
      area: 'set',
      exerciseIndex: 0,
      setIndex: 1,
      field: 'reps',
    })
  })
})

describe('describeDraftSet', () => {
  const loaded = { isBodyweight: false, isAddedWeightEnabled: false }
  const bodyweight = { isBodyweight: true, isAddedWeightEnabled: false }
  const added = { isBodyweight: true, isAddedWeightEnabled: true }

  it.each([
    ['a loaded set', loaded, { weight: '50', reps: '5' }, '50 kg × 5'],
    ['a bodyweight set', bodyweight, { weight: '', reps: '12' }, '12 reps'],
    ['added weight', added, { weight: '10', reps: '8' }, '+10 kg × 8'],
    ['a weight alone', loaded, { weight: '50', reps: '' }, '50 kg'],
    ['an empty set', loaded, { weight: ' ', reps: '' }, ''],
  ])('describes %s', (_scenario, exercise, set, expected) => {
    expect(describeDraftSet(exercise, set)).toBe(expected)
  })
})

describe('describeHeadingWhen', () => {
  const now = new Date(2026, 9, 4, 9, 40)

  it('calls the current date today', () => {
    expect(
      describeHeadingWhen({ date: '2026-10-04', startTime: '09:34' }, '', now),
    ).toBe('Today 09.34')
  })

  it('shows another day of this year without the year', () => {
    expect(
      describeHeadingWhen({ date: '2026-10-03', startTime: '07:15' }, '', now),
    ).toBe('3 Oct 07.15')
  })

  it('adds the year for a page from another year', () => {
    expect(
      describeHeadingWhen({ date: '2025-12-31', startTime: '18:00' }, '', now),
    ).toBe('31 Dec 2025 18.00')
  })

  it('adds the finish time when there is one', () => {
    expect(
      describeHeadingWhen(
        { date: '2026-10-03', startTime: '07:15' },
        '08:20',
        now,
      ),
    ).toBe('3 Oct 07.15–08.20')
  })

  it('reads half-cleared fields as they stand instead of failing', () => {
    expect(describeHeadingWhen({ date: '', startTime: '' }, '', now)).toBe(
      'No date',
    )
  })
})

// User story 6 (tasks.md T088): without consent the editor keeps the rest of a
// draft but none of its title, location, notes or bodyweight.
describe('optional details in a draft', () => {
  const heading = {
    date: '2026-09-15',
    startTime: '07:05',
    title: 'Leg day',
    bodyweightKg: '82.4',
    location: 'Home gym',
    notes: 'Knee felt sore',
  }

  it('drops all four details and keeps the date and start time', () => {
    expect(dropOptionalDetails(heading)).toEqual({
      date: '2026-09-15',
      startTime: '07:05',
      title: '',
      bodyweightKg: '',
      location: '',
      notes: '',
    })
  })

  it('leaves the given draft unchanged', () => {
    dropOptionalDetails(heading)

    expect(heading.title).toBe('Leg day')
  })

  it('sends no detail once dropped', () => {
    const exercise = createWorkoutExerciseDraft('b', 's', 1, 'Squat', false)
    const ready = updateSetInExercise(exercise, 's', {
      weight: '100',
      reps: '5',
    })

    const prepared = prepareWorkoutDraft(dropOptionalDetails(heading), [ready])

    expect(prepared.ok && prepared.value.workout).toMatchObject({
      title: null,
      bodyweightKg: null,
      location: null,
      notes: null,
    })
  })

  it('tells a draft with any detail from one with none', () => {
    expect(hasOptionalDetails(heading)).toBe(true)
    expect(
      hasOptionalDetails({ ...dropOptionalDetails(heading), notes: 'x' }),
    ).toBe(true)
    expect(hasOptionalDetails(dropOptionalDetails(heading))).toBe(false)
    // Whitespace alone is what optionalText already sends as null.
    expect(
      hasOptionalDetails({ ...dropOptionalDetails(heading), title: '  ' }),
    ).toBe(false)
  })
})

// A block as the edit page loads it from the server: no lastSet at all.
function loadedBlock(
  clientId: string,
  exerciseId: number | null,
): WorkoutExerciseDraft {
  return {
    clientId,
    exerciseId,
    exerciseName: 'Back Squat',
    isBodyweight: false,
    isAddedWeightEnabled: false,
    sets: [
      { clientId: `${clientId}-1`, weight: '100', reps: '5', isWarmup: false },
    ],
  }
}

describe('withLastTime', () => {
  const lastSet = { weight: 100, reps: 5, isWarmup: false }

  it('gives loaded blocks the set from the session before', () => {
    const block = loadedBlock('b', 42)

    const [result] = withLastTime([block], [{ id: 42, lastSet }])

    expect(result.lastSet).toEqual(lastSet)
    expect(result.sets).toBe(block.sets)
  })

  // A block picked in this visit already carries the answer it was picked with.
  it('keeps a lastSet the block already has', () => {
    const picked = { ...loadedBlock('b', 42), lastSet: null }

    const [result] = withLastTime([picked], [{ id: 42, lastSet }])

    expect(result).toBe(picked)
  })

  it('leaves a new exercise without one and marks a never-logged one null', () => {
    const newExercise = loadedBlock('a', null)
    const unlogged = loadedBlock('b', 7)

    const [first, second] = withLastTime([newExercise, unlogged], [])

    expect(first).toBe(newExercise)
    expect(second.lastSet).toBeNull()
  })
})
