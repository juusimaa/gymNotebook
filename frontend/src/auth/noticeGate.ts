import type { AccountPrivacyState } from '../api/privacy'

// Pure helpers for the notebook's notice gate (specs/001 contracts/ui.md →
// Notice transitions). No fetch, no router, no DOM, so Vitest can cover every
// branch; requireNoticeAcknowledged.ts is the loader that uses them.

// Where the gate lives, and where a signed-in user lands when there is no
// valid place to return to.
export const NOTICE_GATE_PATH = '/account/privacy/notice'
export const DEFAULT_NOTEBOOK_PATH = '/workouts'

// null is "feature off" (a 404 from GET /account/privacy): no gate at all.
export function needsNoticeGate(state: AccountPrivacyState | null): boolean {
  return state !== null && state.requiresAcknowledgement
}

// The notebook routes the gate protects (routes.tsx). Cover, change-password
// and the privacy screens are deliberately not here: they stay reachable
// without acknowledging (contracts/ui.md → Routes and entry points).
export function isNotebookPath(pathname: string): boolean {
  return /^\/(workouts|progress|exercises)(\/|$)/.test(pathname)
}

export function noticeGateUrl(returnTo: string): string {
  return `${NOTICE_GATE_PATH}?returnTo=${encodeURIComponent(returnTo)}`
}

// The gate's returnTo comes from the query string, so anyone can craft a link
// with any value in it. Only a same-origin notebook path is honoured; anything
// else — another origin, a protocol-relative "//host", a "javascript:" URL, a
// non-notebook route — falls back to the default. The value is resolved by
// the URL parser against a placeholder origin rather than pattern-matched,
// because browsers normalise more than a regex anticipates ("/\host" and a
// tab inside "//" both become another host). What's returned is the parsed,
// normalised path, never the raw input.
export function safeReturnPath(raw: string | null): string {
  if (raw === null || !raw.startsWith('/')) {
    return DEFAULT_NOTEBOOK_PATH
  }

  const base = 'https://gate.invalid'
  let url: URL
  try {
    url = new URL(raw, base)
  } catch {
    return DEFAULT_NOTEBOOK_PATH
  }

  if (url.origin !== base || !isNotebookPath(url.pathname)) {
    return DEFAULT_NOTEBOOK_PATH
  }
  return url.pathname + url.search + url.hash
}
