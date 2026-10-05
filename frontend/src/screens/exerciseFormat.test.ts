import { describe, expect, it } from 'vitest'
import { formatLastSet } from './exerciseFormat'

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
