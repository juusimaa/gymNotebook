import { describe, expect, it } from 'vitest'
import { ApiError } from './client'
import { browserUtcOffsetMinutes, isDemoAccountRefusal } from './demo'

describe('browserUtcOffsetMinutes', () => {
  // A stand-in Date: only getTimezoneOffset is read, and its value depends on
  // the machine's zone, which a test can't set for a real Date.
  const at = (timezoneOffset: number) =>
    ({ getTimezoneOffset: () => timezoneOffset }) as Date

  it('turns east of UTC positive, as the API expects', () => {
    // Helsinki in summer: getTimezoneOffset() is -180.
    expect(browserUtcOffsetMinutes(at(-180))).toBe(180)
  })

  it('turns west of UTC negative', () => {
    expect(browserUtcOffsetMinutes(at(300))).toBe(-300)
  })

  it('sends UTC as a plain 0', () => {
    expect(Object.is(browserUtcOffsetMinutes(at(0)), 0)).toBe(true)
  })
})

describe('isDemoAccountRefusal', () => {
  it('matches a 403 demo_account', () => {
    expect(isDemoAccountRefusal(new ApiError(403, 'demo_account'))).toBe(true)
  })

  it.each([
    new ApiError(403, 'account_suspended'),
    new ApiError(400, 'demo_account'),
    new TypeError('Failed to fetch'),
  ])('ignores %o', (err) => {
    expect(isDemoAccountRefusal(err)).toBe(false)
  })
})
