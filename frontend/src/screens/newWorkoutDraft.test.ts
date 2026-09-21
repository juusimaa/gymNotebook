import { describe, expect, it } from 'vitest'
import {
  createEmptySetDraft,
  createInitialHeadingDraft,
} from './newWorkoutDraft'

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
