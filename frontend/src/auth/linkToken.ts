import { ApiError } from '../api/client'

// Links in emails carry their token in the URL fragment: /verify-email#token=…
// (specs/002 FR-010). Browsers never send the part after "#" to a server or put
// it in a Referer header, so the token can't land in an access log on its way
// here. The screen reads it once and then removes it from the address bar.

// The token from a location hash such as "#token=abc". null when there is none,
// so "opened the page without a token" and "opened a broken link" are the same
// case for the screen. URLSearchParams also undoes the %-escaping the email
// template applied.
export function readFragmentToken(hash: string): string | null {
  const params = new URLSearchParams(
    hash.startsWith('#') ? hash.slice(1) : hash,
  )
  const token = params.get('token')?.trim()
  return token === undefined || token === '' ? null : token
}

// Drops the fragment from the current history entry, so the token doesn't stay
// in the address bar, in the back/forward history, or in a bookmark of the page.
// replaceState rather than navigate: nothing should re-render or re-run, and
// the router's own entry state is kept as it is.
export function removeFragmentFromUrl(): void {
  if (window.location.hash === '') {
    return
  }
  window.history.replaceState(
    window.history.state,
    '',
    window.location.pathname + window.location.search,
  )
}

export type LinkFailure = 'expired' | 'invalid'

// What a link route's 400 means (specs/002 contracts/api.md): "expired" gets its
// own screen, because asking for a fresh link fixes it; every other 400 is
// "this link doesn't work". null for anything that isn't a link failure — a 429,
// a 500, no connection — which the screen reports as a problem worth retrying.
export function linkFailure(error: unknown): LinkFailure | null {
  if (!(error instanceof ApiError) || error.status !== 400) {
    return null
  }
  return error.code === 'expired' ? 'expired' : 'invalid'
}
