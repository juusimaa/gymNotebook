import { describe, expect, it } from 'vitest'
import { describeAuthError, isEmailNotVerified } from './authErrors'
import { ApiError } from './client'

// One case per status the backend can answer /auth/login, /auth/register and
// /auth/verification with (the .Produces(...) lists in Program.cs), plus the two
// fallthroughs. `it.each` runs the same assertion over a table so the mapping
// reads as a table.
describe('describeAuthError', () => {
  it.each([
    [400, 'Check the email address and password'],
    [401, 'Email or password is wrong'],
    [403, 'Invite code is wrong or missing'],
    [429, 'Too many attempts - wait a minute and try again'],
  ])('maps a %i to what the user should do', (status, message) => {
    expect(describeAuthError(new ApiError(status))).toBe(message)
  })

  // Login's 403s carry a code; register's invite-code 403 doesn't. The same
  // status must produce different copy.
  it('maps a 403 account_suspended to the privacy contact path', () => {
    expect(describeAuthError(new ApiError(403, 'account_suspended'))).toBe(
      'Sign-in is paused for this account. Please contact the privacy contact to resolve it',
    )
  })

  it('maps a 403 email_not_verified to the inbox', () => {
    expect(describeAuthError(new ApiError(403, 'email_not_verified'))).toBe(
      'Confirm your email address first - the link is in your inbox',
    )
  })

  // A status we don't map still means the server answered — the message must say
  // so, and carry the number, rather than blame the network.
  it('names the status for an unexpected server answer', () => {
    expect(describeAuthError(new ApiError(500))).toBe('The server answered 500')
  })

  // What fetch itself rejects with when the API is down or the URL is wrong.
  it('blames the connection when the error is not from the API', () => {
    expect(describeAuthError(new TypeError('Failed to fetch'))).toBe(
      'Unable to reach the server, please check your connection and try again',
    )
  })
})

// The sign-in screen's cue to show "check your inbox" rather than an error.
describe('isEmailNotVerified', () => {
  it('is true only for a 403 with the email_not_verified code', () => {
    expect(isEmailNotVerified(new ApiError(403, 'email_not_verified'))).toBe(
      true,
    )
    expect(isEmailNotVerified(new ApiError(403, 'account_suspended'))).toBe(
      false,
    )
    expect(isEmailNotVerified(new ApiError(403))).toBe(false)
    expect(isEmailNotVerified(new TypeError('Failed to fetch'))).toBe(false)
  })
})
