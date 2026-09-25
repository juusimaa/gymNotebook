import { describe, expect, it } from 'vitest'
import { describeAuthError } from './authErrors'
import { ApiError } from './client'

// One case per status the backend can answer /auth/login and /auth/register with
// (the .Produces(...) lists in Program.cs), plus the two fallthroughs. `it.each`
// runs the same assertion over a table so the mapping reads as a table.
describe('describeAuthError', () => {
  it.each([
    [400, 'Username and password are required'],
    [401, 'Invalid username or password'],
    [403, 'Invite code is wrong or missing'],
    [409, 'Username already taken'],
    [429, 'Too many attempts - wait a minute and try again'],
  ])('maps a %i to what the user should do', (status, message) => {
    expect(describeAuthError(new ApiError(status))).toBe(message)
  })

  // Login's 403 for a suspended account carries a code; register's invite-code
  // 403 doesn't. The same status must produce different copy.
  it('maps a 403 account_suspended to the privacy contact path', () => {
    expect(describeAuthError(new ApiError(403, 'account_suspended'))).toBe(
      'Sign-in is paused for this account. Please contact the privacy contact to resolve it',
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
