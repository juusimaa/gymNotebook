import { describe, expect, it } from 'vitest'
import type { TemplateDetailResponse } from '../api/templates'
import { createWorkoutExerciseDraft } from './newWorkoutDraft'
import {
  createTemplateDraft,
  describeTemplateCounts,
  describeTemplateNameTaken,
  findTakenTemplateName,
  prepareTemplateDraft,
} from './templateDraft'

function sequentialIds() {
  let next = 0
  return () => {
    next += 1
    return `id-${next}`
  }
}

const pushA: TemplateDetailResponse = {
  id: 3,
  name: 'Push A',
  exercises: [
    {
      id: 9,
      exerciseId: 12,
      exerciseName: 'Bench Press',
      isBodyweight: false,
      sets: [
        { id: 31, setNumber: 1, weight: 60, reps: 8, isWarmup: true },
        { id: 32, setNumber: 2, weight: 80.5, reps: 5, isWarmup: false },
      ],
    },
    {
      id: 10,
      exerciseId: 14,
      exerciseName: 'Dips',
      isBodyweight: true,
      sets: [{ id: 33, setNumber: 1, weight: null, reps: 10, isWarmup: false }],
    },
  ],
}

describe('createTemplateDraft', () => {
  it('createTemplateDraft_LoadedTemplate_KeepsNameBlocksAndFigures', () => {
    // Arrange / Act
    const draft = createTemplateDraft(pushA, sequentialIds())

    // Assert
    expect(draft.name).toBe('Push A')
    expect(draft.exercises).toEqual([
      {
        clientId: 'id-1',
        exerciseId: 12,
        exerciseName: 'Bench Press',
        isBodyweight: false,
        isAddedWeightEnabled: false,
        sets: [
          {
            clientId: 'id-2',
            weight: '60',
            reps: '8',
            isWarmup: true,
            status: 'planned',
          },
          {
            clientId: 'id-3',
            weight: '80.5',
            reps: '5',
            isWarmup: false,
            status: 'planned',
          },
        ],
      },
      {
        clientId: 'id-4',
        exerciseId: 14,
        exerciseName: 'Dips',
        isBodyweight: true,
        isAddedWeightEnabled: false,
        sets: [
          {
            clientId: 'id-5',
            weight: '',
            reps: '10',
            isWarmup: false,
            status: 'planned',
          },
        ],
      },
    ])
  })

  it('createTemplateDraft_BodyweightWithAddedWeight_ShowsWeightField', () => {
    // Arrange
    const template: TemplateDetailResponse = {
      ...pushA,
      exercises: [
        {
          ...pushA.exercises[1],
          sets: [{ id: 1, setNumber: 1, weight: 10, reps: 6, isWarmup: false }],
        },
      ],
    }

    // Act
    const draft = createTemplateDraft(template, sequentialIds())

    // Assert
    expect(draft.exercises[0].isAddedWeightEnabled).toBe(true)
  })
})

describe('prepareTemplateDraft', () => {
  it('prepareTemplateDraft_LoadedTemplate_SendsTrimmedNameAndSetsWithoutStatus', () => {
    // Arrange
    const draft = createTemplateDraft(pushA, sequentialIds())

    // Act
    const result = prepareTemplateDraft({ ...draft, name: '  Push A  ' })

    // Assert
    expect(result).toEqual({
      ok: true,
      value: {
        name: 'Push A',
        exercises: [
          {
            exerciseName: 'Bench Press',
            sets: [
              { weight: 60, reps: 8, isWarmup: true },
              { weight: 80.5, reps: 5, isWarmup: false },
            ],
          },
          {
            exerciseName: 'Dips',
            sets: [{ weight: null, reps: 10, isWarmup: false }],
          },
        ],
      },
    })
  })

  it('prepareTemplateDraft_BlankName_PointsAtName', () => {
    // Arrange
    const draft = createTemplateDraft(pushA, sequentialIds())

    // Act
    const result = prepareTemplateDraft({ ...draft, name: '   ' })

    // Assert
    expect(result).toEqual({
      ok: false,
      at: { area: 'name' },
      message: 'Name the template.',
    })
  })

  it('prepareTemplateDraft_NoExercises_AsksForOne', () => {
    // Arrange / Act
    const result = prepareTemplateDraft({ name: 'Push A', exercises: [] })

    // Assert
    expect(result).toEqual({
      ok: false,
      at: { area: 'exercises' },
      message: 'Add at least one exercise.',
    })
  })

  it('prepareTemplateDraft_SetWithoutReps_PointsAtThatSet', () => {
    // Arrange
    const block = createWorkoutExerciseDraft(
      'block-1',
      'set-1',
      12,
      'Bench Press',
      false,
      null,
      null,
      'planned',
    )
    const exercise = {
      ...block,
      sets: [{ ...block.sets[0], weight: '60', reps: '' }],
    }

    // Act
    const result = prepareTemplateDraft({
      name: 'Push A',
      exercises: [exercise],
    })

    // Assert
    expect(result).toMatchObject({
      ok: false,
      at: { area: 'set', exerciseIndex: 0, setIndex: 0, field: 'reps' },
    })
  })
})

describe('findTakenTemplateName', () => {
  const templates = [
    { id: 3, name: 'Push A' },
    { id: 4, name: 'Pull A' },
  ]

  it('findTakenTemplateName_DifferentCaseAndSpacing_ReturnsExistingSpelling', () => {
    expect(findTakenTemplateName(' push  a ', templates, null)).toBe('Push A')
  })

  it('findTakenTemplateName_OnlyMatchIsItself_ReturnsTypedName', () => {
    expect(findTakenTemplateName('push a', templates, 3)).toBe('push a')
  })

  it('findTakenTemplateName_NotInList_ReturnsTrimmedTypedName', () => {
    expect(findTakenTemplateName(' Legs ', [], null)).toBe('Legs')
  })
})

describe('template copy', () => {
  it('describeTemplateNameTaken_PaddedName_NamesItTrimmed', () => {
    expect(describeTemplateNameTaken(' Push A ')).toBe(
      'You already have a template called Push A.',
    )
  })

  it('describeTemplateCounts_SingularAndPlural_ReadsAsInTheMock', () => {
    expect(describeTemplateCounts({ exerciseCount: 3, setCount: 9 })).toBe(
      '3 exercises · 9 sets',
    )
    expect(describeTemplateCounts({ exerciseCount: 1, setCount: 1 })).toBe(
      '1 exercise · 1 set',
    )
  })
})
