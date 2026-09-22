import { describe, expect, it } from 'vitest'
import {
  addEmptySetToExercise,
  createEmptySetDraft,
  createExistingWorkoutDraft,
  createInitialHeadingDraft,
  createLocalEndedAt,
  createWorkoutExerciseDraft,
  prepareWorkoutDraft,
  removeSetFromExercise,
  updateSetInExercise,
} from './newWorkoutDraft'

function createTwoSetExercise() {
  const exercise = createWorkoutExerciseDraft(
    'block-1',
    'set-1',
    42,
    'Back Squat',
    false,
  )

  return addEmptySetToExercise(exercise, 'set-2')
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
})

describe('set draft operations', () => {
  it('adds an empty set without changing the original exercise', () => {
    const original = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      42,
      'Back Squat',
      false,
    )

    const result = addEmptySetToExercise(original, 'set-2')

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
    ['no exercises', [], 'Add at least one exercise.'],
    [
      'missing reps',
      [{ weight: '100', reps: '' }],
      'Back Squat, set 1: reps must be a whole number greater than zero.',
    ],
    [
      'missing loaded weight',
      [{ weight: '', reps: '5' }],
      'Back Squat, set 1: enter a valid weight.',
    ],
  ])('rejects %s', (_scenario, setValues, expectedMessage) => {
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
      message: expectedMessage,
    })
  })
})
