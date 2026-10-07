import { ApiError } from './client'

// Turns whatever login()/register()/resendVerification() threw into the sentence
// shown under the form. Three outcomes, not two: a status we expect maps to what
// the user should do differently; a status we don't (a 500, a 404 from a wrong
// VITE_API_URL) still means the server answered, and says so with the number;
// anything that isn't an ApiError — fetch's TypeError, in practice — means the
// request never got there. Pure and DOM-free, so Vitest tests it directly.
export function describeAuthError(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return 'Check the email address and password'
      case 401:
        // One message for "no such account" and "wrong password": the API
        // answers both the same on purpose (specs/002 FR-003).
        return 'Email or password is wrong'
      case 403:
        // Login's 403s carry a code, and only after the password was right
        // (specs/001 and specs/002 contracts). account_suspended gets neutral
        // copy pointing to the privacy contact, since the operator resolves a
        // suspension with the user. email_not_verified normally never reaches
        // here — the screen shows "check your inbox" instead (see
        // isEmailNotVerified) — but has copy in case a caller doesn't. Without
        // a code, the 403 is register's invite-code check.
        if (error.code === 'account_suspended') {
          return 'Sign-in is paused for this account. Please contact the privacy contact to resolve it'
        }
        if (error.code === EMAIL_NOT_VERIFIED) {
          return 'Confirm your email address first - the link is in your inbox'
        }
        return 'Invite code is wrong or missing'
      case 429:
        return 'Too many attempts - wait a minute and try again'
      default:
        return `The server answered ${error.status}`
    }
  }
  return 'Unable to reach the server, please check your connection and try again'
}

const EMAIL_NOT_VERIFIED = 'email_not_verified'

// Right password, unconfirmed address (specs/002 FR-003): not an error to show,
// but the cue to switch the sign-in screen to "check your inbox".
export function isEmailNotVerified(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    error.status === 403 &&
    error.code === EMAIL_NOT_VERIFIED
  )
}

// Shown on the sign-in screen after the session ended on a write (research R4,
// Q5, contracts/ui.md → Coordination states). The server may have committed the
// change before it found the session revoked and answered 401, so repeating it
// blindly could save it twice.
export const INTERRUPTED_WRITE_NOTICE =
  'You were signed out while a change was being saved. It may already have been saved, so check your notebook before entering it again.'
