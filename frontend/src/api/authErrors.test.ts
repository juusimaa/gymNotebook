import { describe, expect, it } from 'vitest'
import {
  describeAuthError,
  isCaptchaFailure,
  isEmailNotVerified,
} from './authErrors'
import { ApiError } from './client'

// One case per status the backend can answer /auth/login, /auth/register and
// /auth/verification with (the .Produces(...) lists in Program.cs), plus the two
// fallthroughs. `it.each` runs the same assertion over a table so the mapping
// reads as a table.
describe('describeAuthError', () => {
  it.each([
    [400, 'Check the email address and password'],
    [401, 'Email or password is wrong'],
    [429, 'Too many attempts - wait a minute and try again'],
  ])('maps a %i to what the user should do', (status, message) => {
    expect(describeAuthError(new ApiError(status))).toBe(message)
  })

  // The same status must produce different copy depending on the code.
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

  it('maps a 400 captcha to the bot check, not the fields', () => {
    expect(describeAuthError(new ApiError(400, 'captcha'))).toBe(
      "The check didn't pass. Try again.",
    )
  })

  // No route sends a 403 without a code any more (the invite code is gone).
  it('names the status for a 403 without a known code', () => {
    expect(describeAuthError(new ApiError(403))).toBe('The server answered 403')
  })

  // A status we don't map still means the server answered — the message must say
  // so, and carry the number, rather than blame the network.
  it('names the status for an unexpected server answer', () => {
    expect(describeAuthError(new ApiError(500))).toBe('The server answered 500')
  })

  // What fetch itself rejects with when the API is down or the URL is wrong.
  it('blames the connection when the error is not from the API', () => {
    expect(describeAuthError(new TypeError('Failed to fetch'))).toBe(
      "Can't reach the notebook. Check your connection and try again.",
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

describe('isCaptchaFailure', () => {
  it('is true only for a 400 with the captcha code', () => {
    expect(isCaptchaFailure(new ApiError(400, 'captcha'))).toBe(true)
    expect(isCaptchaFailure(new ApiError(400, 'invalid_request'))).toBe(false)
    expect(isCaptchaFailure(new ApiError(400))).toBe(false)
    expect(isCaptchaFailure(new TypeError('Failed to fetch'))).toBe(false)
  })
})
