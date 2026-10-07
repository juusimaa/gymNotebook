import type { LinkFailure } from './linkToken'

// How another screen opens /login, passed in router state rather than the URL so
// that nothing about it ends up in history or a bookmark:
//
//   /verify-email   { email }                  sign in with the confirmed
//                                               address already filled in
//   /reset-password { mode: 'forgot', reason } the reset link didn't work:
//                                               open "Forgot password" and
//                                               say why
//
// Router state is `any` and survives reloads, so it's read defensively: any
// shape this doesn't recognise opens a plain sign-in.
export interface LoginEntry {
  email: string
  mode: 'signIn' | 'forgot'
  // A line to show above the form, or null.
  notice: string | null
}

export interface LoginEntryState {
  email?: string
  mode?: 'forgot'
  reason?: LinkFailure
}

const RESET_LINK_NOTICES: Record<LinkFailure, string> = {
  expired:
    'That reset link has expired. Links work for one hour - send yourself a new one.',
  invalid:
    'That reset link no longer works. Each link works once - send yourself a new one.',
}

export function readLoginEntry(state: unknown): LoginEntry {
  const entry: LoginEntry = { email: '', mode: 'signIn', notice: null }
  if (typeof state !== 'object' || state === null) {
    return entry
  }
  if ('email' in state && typeof state.email === 'string') {
    entry.email = state.email
  }
  if ('mode' in state && state.mode === 'forgot') {
    entry.mode = 'forgot'
    if (
      'reason' in state &&
      (state.reason === 'expired' || state.reason === 'invalid')
    ) {
      entry.notice = RESET_LINK_NOTICES[state.reason]
    }
  }
  return entry
}
