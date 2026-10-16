import type { AuthResponse } from './auth'
import { ApiError, request } from './client'

// Wire calls for demo mode (specs/007 contracts/api.md). The API maps both
// routes only when DEMO_MODE_ENABLED is "true"; the screens offer them only
// when the frontend's own switch says so (screens/demoModeEnabled.ts), or,
// after sign-in, when /auth/me carries a demoExpiresAt.

// The browser's offset from UTC in minutes, east positive: what the API wants
// as utcOffsetMinutes, so the generated notebook's dates and times fall on the
// visitor's own days. getTimezoneOffset() has the opposite sign (west
// positive), hence the minus.
export function browserUtcOffsetMinutes(now: Date = new Date()): number {
  // `|| 0` turns UTC's -0 into 0, so the JSON says 0 rather than relying on
  // -0 serialising as "0".
  return -now.getTimezoneOffset() || 0
}

// turnstileToken is left out when the check is off (no site key), as on
// register. Answers the same { token } as login, which the caller stores.
// `local`: no session is involved, and a stale one in this browser has
// nothing to do with starting a new demo.
export function startDemo(body: {
  turnstileToken?: string
}): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/demo', {
    method: 'POST',
    body: { ...body, utcOffsetMinutes: browserUtcOffsetMinutes() },
    unauthorized: 'local',
  })
}

// Replaces the whole notebook with a newly generated one (204). The session,
// the privacy answers and the demo's end are kept. A 401 here means the demo
// has ended, which signs out like any ended session.
export function resetDemo(): Promise<void> {
  return request<void>('/account/demo/reset', {
    method: 'POST',
    body: { utcOffsetMinutes: browserUtcOffsetMinutes() },
  })
}

// The 403 a demo account gets from change password, export and account
// deletion (specs/007 FR-012). The screens hide those actions for a demo up
// front; this covers a deep link or a tab left open.
export const DEMO_ACCOUNT = 'demo_account'

export function isDemoAccountRefusal(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    error.status === 403 &&
    error.code === DEMO_ACCOUNT
  )
}

export const DEMO_ACCOUNT_REFUSAL = "This isn't available in a demo."
