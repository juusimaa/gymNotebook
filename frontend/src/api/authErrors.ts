import { ApiError } from './client'

// Turns whatever login()/register() threw into the sentence shown under the form.
// Three outcomes, not two: a status we expect maps to what the user should do
// differently; a status we don't (a 500, a 404 from a wrong VITE_API_URL) still
// means the server answered, and says so with the number; anything that isn't an
// ApiError — fetch's TypeError, in practice — means the request never got there.
// Pure and DOM-free, so it's the first thing the Vitest job actually tests.
export function describeAuthError(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return 'Username and password are required'
      case 401:
        return 'Invalid username or password'
      case 403:
        // Only login sends a code with its 403, and only after the password was
        // right (specs/001 contracts/ui.md): neutral copy pointing to the privacy
        // contact, since the operator resolves a suspension with the user. Without
        // a code, the 403 is register's invite-code check.
        return error.code === 'account_suspended'
          ? 'Sign-in is paused for this account. Please contact the privacy contact to resolve it'
          : 'Invite code is wrong or missing'
      case 409:
        return 'Username already taken'
      case 429:
        return 'Too many attempts - wait a minute and try again'
      default:
        return `The server answered ${error.status}`
    }
  }
  return 'Unable to reach the server, please check your connection and try again'
}
