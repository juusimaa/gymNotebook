import { describe, expect, it } from 'vitest'
import { filterExercisesByName, formatLastSet } from './exerciseFormat'

describe('formatLastSet', () => {
  it('formats a working set without a tag', () => {
    expect(
      formatLastSet({ weight: 100, reps: 5, isWarmup: false }, false),
    ).toBe('100 kg × 5')
  })

  it('marks a warm-up so it does not read as working weight', () => {
    expect(formatLastSet({ weight: 40, reps: 8, isWarmup: true }, false)).toBe(
      '40 kg × 8 · warm-up',
    )
    expect(
      formatLastSet({ weight: null, reps: 12, isWarmup: true }, true),
    ).toBe('12 reps · warm-up')
  })

  it('prefixes added weight on a bodyweight exercise', () => {
    expect(formatLastSet({ weight: 10, reps: 6, isWarmup: false }, true)).toBe(
      '+10 kg × 6',
    )
  })
})

describe('filterExercisesByName', () => {
  const exercises = [
    { name: 'Back Squat' },
    { name: 'Takakyykky' },
    { name: 'Ylätalja  vastaotteella' },
  ]

  it('returns every exercise for an empty or blank query', () => {
    expect(filterExercisesByName(exercises, '')).toEqual(exercises)
    expect(filterExercisesByName(exercises, '   ')).toEqual(exercises)
  })

  it('matches any part of the name, ignoring case and spacing', () => {
    expect(filterExercisesByName(exercises, ' TAKA')).toEqual([
      { name: 'Takakyykky' },
    ])
    expect(filterExercisesByName(exercises, 'talja vasta')).toEqual([
      { name: 'Ylätalja  vastaotteella' },
    ])
  })

  it('returns nothing when no name matches', () => {
    expect(filterExercisesByName(exercises, 'bench')).toEqual([])
  })
})
