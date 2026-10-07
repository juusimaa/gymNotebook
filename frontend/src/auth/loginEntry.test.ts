import { describe, expect, it } from 'vitest'
import { readLoginEntry } from './loginEntry'

describe('readLoginEntry', () => {
  it.each([undefined, null, 'forgot', 42])(
    'opens a plain sign-in for state %j',
    (state) => {
      expect(readLoginEntry(state)).toEqual({
        email: '',
        mode: 'signIn',
        notice: null,
      })
    },
  )

  // From /verify-email: sign in with the confirmed address filled in.
  it('pre-fills the email', () => {
    expect(readLoginEntry({ email: 'a@example.test' })).toEqual({
      email: 'a@example.test',
      mode: 'signIn',
      notice: null,
    })
  })

  // From /reset-password when the link didn't work.
  it.each([
    ['expired', 'That reset link has expired.'],
    ['invalid', 'That reset link no longer works.'],
  ])('opens Forgot password with the %s reason', (reason, start) => {
    const entry = readLoginEntry({ mode: 'forgot', reason })
    expect(entry.mode).toBe('forgot')
    expect(entry.notice?.startsWith(start)).toBe(true)
  })

  it('ignores a reason it does not know, and a reason without the mode', () => {
    expect(
      readLoginEntry({ mode: 'forgot', reason: 'other' }).notice,
    ).toBeNull()
    expect(readLoginEntry({ reason: 'expired' })).toEqual({
      email: '',
      mode: 'signIn',
      notice: null,
    })
  })

  it('ignores an email that is not a string', () => {
    expect(readLoginEntry({ email: 5 }).email).toBe('')
  })
})
