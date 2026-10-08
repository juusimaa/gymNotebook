import {
  getToken,
  isTokenExpired,
  setToken,
  shouldRenew,
  shouldRenewOnHide,
} from '../auth/token'

// Vite inlines import.meta.env.VITE_* at build time (PLAN.md, "The VITE_API_URL
// trap"). The strict ImportMetaEnv in vite-env.d.ts makes the *name* a compile
// error if mistyped, but an unset variable is still undefined at runtime — so this
// is checked once, at module load, and the app fails at first paint with the key
// named rather than on the first click. Milestone 6's runtime override
// (window.__API_URL__, rendered by the container's entrypoint) goes here, ahead of
// the build-time value, so the lookup keeps exactly one home.
const runtimeConfigured =
  typeof window === 'undefined' ? undefined : window.__API_URL__

const configured = runtimeConfigured ?? import.meta.env.VITE_API_URL
if (configured === undefined) {
  throw new Error('VITE_API_URL is not configured.')
}
// Tolerate a trailing slash in .env so a path of "/auth/login" never becomes
// "//auth/login".
const baseUrl = configured.replace(/\/$/, '')

// A non-2xx response. Most backend errors are a bare status with no body
// (Results.BadRequest(), Results.Conflict(), ...), so the status is usually the
// whole error. A few carry a JSON `{ "code": "..." }` (ErrorResponse in the API)
// where the same status can mean different things — a 403 at login is
// "account_suspended" or "email_not_verified", a 400 at register is a malformed
// field or a failed bot check ("captcha") — and `code` holds it when present. Extending Error is what lets a screen tell "the server
// said no" apart from "the request never got there": `err instanceof ApiError` on
// the one hand, fetch's own TypeError on the other.
export class ApiError extends Error {
  // Declared as fields rather than `constructor(public readonly status)`
  // parameter properties: tsconfig has erasableSyntaxOnly on, which only allows
  // TypeScript syntax that can be deleted to leave valid JavaScript, and
  // parameter properties generate an assignment.
  readonly status: number
  readonly code: string | undefined
  // A finer-grained cause under the same code, for the one route that sends
  // it: a rejected backup file's `{ "code": "backup_invalid", "reason": ... }`.
  readonly reason: string | undefined

  constructor(status: number, code?: string, reason?: string) {
    super(`API responded ${status}`)
    this.status = status
    this.code = code
    this.reason = reason
  }
}

// The 403 a workout save gets when it carries a title, location, notes or
// bodyweight the account hasn't allowed (specs/001 user story 6) — for example
// from a tab still open after consent was withdrawn elsewhere. It is not a
// session problem: nothing signs out, and the editor keeps the draft and drops
// only those details (contracts/ui.md → Rejected save).
export const OPTIONAL_DETAILS_CONSENT_REQUIRED =
  'optional_details_consent_required'

export function isOptionalDetailsConsentRequired(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    error.status === 403 &&
    error.code === OPTIONAL_DETAILS_CONSENT_REQUIRED
  )
}

// Reads the `code` (and `reason`, when present) out of an error response. Only
// a JSON body is parsed, and a body that isn't the expected shape just means
// "no code" — the status alone is still a complete error.
async function readErrorBody(
  response: Response,
): Promise<{ code?: string; reason?: string }> {
  if (!response.headers.get('Content-Type')?.includes('application/json')) {
    return {}
  }
  try {
    const body: unknown = await response.json()
    if (typeof body === 'object' && body !== null) {
      const fields = body as Record<string, unknown>
      return {
        code: typeof fields.code === 'string' ? fields.code : undefined,
        reason: typeof fields.reason === 'string' ? fields.reason : undefined,
      }
    }
  } catch {
    // Malformed JSON on an error response: fall back to the status alone.
  }
  return {}
}

interface RequestOptions {
  method?: string
  // Anything JSON.stringify can serialise. `unknown` rather than `object` so a
  // caller must mean it; the wire types in api/auth.ts etc. are what give it shape.
  body?: unknown
  // A body that is already JSON, sent byte for byte instead of `body`: a backup
  // file being restored, which must reach the server exactly as it was saved.
  jsonFile?: Blob
  // Lets a caller cancel the request, e.g. a download the user walks away from.
  signal?: AbortSignal
  // Who handles a 401 (specs/001 contracts/ui.md → Invalidation). "session", the
  // default: the session is over, so the app-wide handler (auth/invalidation.ts)
  // signs out before the ApiError reaches the caller. "local": the caller owns
  // it. Change-password needs that because its wrong-current-password answer is
  // also a 401, and account deletion because its 401 must never be read as
  // "deleted" and gets its own message.
  unauthorized?: 'session' | 'local'
  // Whether the request can change stored data, for the warning shown after a
  // 401 on a write: the change may already be saved (research R4, Q5). Anything
  // but GET counts unless the caller says otherwise (the export is a POST that
  // only reads).
  changesData?: boolean
}

// What the app-wide handler learns about a 401 that ended the session.
export interface SessionEnded {
  changesData: boolean
  // The token had simply run out, rather than being invalidated by a password
  // change or account deletion. An expiry keeps the editor draft.
  tokenExpired: boolean
}

let sessionEndedHandler: ((event: SessionEnded) => void) | null = null

// Registered once at startup by auth/invalidation.ts. A setter rather than an
// import, because invalidation.ts itself imports from this file.
export function onSessionEnded(
  handler: ((event: SessionEnded) => void) | null,
): void {
  sessionEndedHandler = handler
}

// Every request between fetch and the end of reading its body, so an
// invalidation can abort them all: nothing personal arrives after sign-out.
const inFlight = new Set<AbortController>()

export function abortPendingRequests(): void {
  for (const controller of [...inFlight]) {
    controller.abort()
  }
}

// Token renewal (specs/003 D4–D7). A session token lives 30 minutes; past half
// of that, the next request first trades it for a fresh one at POST /auth/token,
// so a long gym session never meets the sign-in screen. The server stops
// renewing 12 hours after the password was last entered (403 renewal_refused);
// the current token then just runs out, and its 401 ends the session as before.
//
// Renewal is a convenience, never a reason for the actual request to fail: any
// problem here — no connection, a 429, a 401 — is swallowed, and the request
// goes ahead with the token it has. If that token is no good, the request's own
// 401 is what ends the session, through the one path that already handles it.

// One renewal at a time. A screen that loads three things at once, or a save
// racing a visibility change, all wait for the same POST.
let renewal: Promise<void> | null = null

// The token the server refused to renew (the 12-hour cap). Not asked again: it
// still works until it expires, and asking would only spend the auth rate limit.
// A new sign-in stores a different token, which renews normally.
let refusedToken: string | null = null

// Renews the stored token if it's due. Resolves once the stored token is the
// best one available; never rejects.
export function renewTokenIfDue(now: Date = new Date()): Promise<void> {
  const token = getToken()
  if (token === null || token === refusedToken || !shouldRenew(token, now)) {
    return renewal ?? Promise.resolve()
  }
  renewal ??= renew(token, false).finally(() => {
    renewal = null
  })
  return renewal
}

// The renewal sent as the tab is hidden (see installBackgroundRenewal). Kept out
// of the shared `renewal` on purpose: the editor flushes its unsaved sets on the
// same event, and that save must go out at once rather than wait behind a
// renewal the page may be frozen before finishing. Skipped while a shared
// renewal is already under way, since that one stores a fresh token anyway.
function renewOnHide(now: Date): void {
  const token = getToken()
  if (
    renewal !== null ||
    token === null ||
    token === refusedToken ||
    !shouldRenewOnHide(token, now)
  ) {
    return
  }
  void renew(token, true)
}

async function renew(token: string, keepalive: boolean): Promise<void> {
  // Tracked like any request, so a sign-out aborts it and no token can be
  // stored after the session ended.
  const controller = new AbortController()
  inFlight.add(controller)
  try {
    // Plain fetch rather than send(): send() would come back here first, and a
    // 401 on this POST must not end the session by itself (see above).
    const response = await fetch(baseUrl + '/auth/token', {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}` },
      // Lets the request outlive a page that is being hidden or frozen.
      keepalive,
      signal: controller.signal,
    })
    if (response.ok) {
      const body = (await response.json()) as { token: string }
      // Only replace the token this renewed. If the user signed out, or signed
      // in as someone else in another tab, meanwhile, theirs stays.
      if (getToken() === token) {
        setToken(body.token)
      }
      return
    }
    const { code } = await readErrorBody(response)
    if (response.status === 403 && code === 'renewal_refused') {
      refusedToken = token
    }
  } catch {
    // Offline or aborted: keep the current token; the next request tries again.
  } finally {
    inFlight.delete(controller)
  }
}

// How often the visible tab checks whether its token is due. A minute is far
// inside the 15 minutes between half-life and expiry, and a check without a
// renewal is only a localStorage read.
const VISIBLE_CHECK_MS = 60 * 1000

// Renews without waiting for a request (D7), so neither an open screen nor a
// locked phone runs out while the session is still wanted:
// - While the tab is visible, it checks every minute and renews past half-life.
//   A page left open between sets, with nothing to save, stays signed in.
// - As the tab is hidden, it renews any token at least five minutes old, so the
//   phone sleeps on a nearly fresh one. This is best effort: `keepalive` gets the
//   request out, but a phone that freezes the page at once may never store the
//   answer. Then the token simply keeps the time it had.
// - As the tab comes back, it renews past half-life before the next save.
// Hidden tabs run no check; browsers throttle their timers anyway.
// Wired once at startup (main.tsx); returns an uninstall function, for tests.
export function installBackgroundRenewal(
  target: Pick<
    Document,
    'addEventListener' | 'removeEventListener' | 'visibilityState'
  > = document,
  timers: Pick<typeof globalThis, 'setInterval' | 'clearInterval'> = globalThis,
): () => void {
  let check: ReturnType<typeof setInterval> | undefined

  const startChecking = () => {
    check ??= timers.setInterval(() => void renewTokenIfDue(), VISIBLE_CHECK_MS)
  }
  const stopChecking = () => {
    if (check !== undefined) {
      timers.clearInterval(check)
      check = undefined
    }
  }

  const onVisibilityChange = () => {
    if (target.visibilityState === 'visible') {
      void renewTokenIfDue()
      startChecking()
    } else {
      stopChecking()
      renewOnHide(new Date())
    }
  }

  if (target.visibilityState === 'visible') {
    startChecking()
  }
  target.addEventListener('visibilitychange', onVisibilityChange)
  return () => {
    stopChecking()
    target.removeEventListener('visibilitychange', onVisibilityChange)
  }
}

// The one fetch wrapper every api/*.ts file goes through: base URL, JSON in,
// bearer token when there is one, non-2xx turned into a thrown ApiError. `read`
// turns the response into the result while the request is still tracked (and
// abortable), so a caller that reads a long body itself — the notebook export —
// can still be cut off by a sign-out; everything else uses request() below.
// Deliberately thin — no retries, no timeout — so each of those stays a
// decision made in one place.
export async function send<T>(
  path: string,
  init: RequestOptions,
  read: (response: Response) => Promise<T>,
): Promise<T> {
  const headers: Record<string, string> = {}

  // Only when there's a body: a GET carrying Content-Type is a "non-simple"
  // request and would cost a preflight for nothing.
  if (init.body !== undefined || init.jsonFile !== undefined) {
    headers['Content-Type'] = 'application/json'
  }

  // Our own controller, so abortPendingRequests() can cancel this request; the
  // caller's signal, if any, is forwarded to it. Registered before the renewal
  // below, so a sign-out while this waits for it still cancels the request.
  const controller = new AbortController()
  if (init.signal?.aborted) {
    controller.abort(init.signal.reason)
  }
  init.signal?.addEventListener(
    'abort',
    () => controller.abort(init.signal?.reason),
    {
      once: true,
    },
  )
  inFlight.add(controller)

  const method = init.method ?? 'GET'
  try {
    // Before reading the token, so the request carries the renewed one.
    await renewTokenIfDue()
    // Rejects as fetch would, had it been aborted.
    controller.signal.throwIfAborted()
    const token = getToken()
    if (token !== null) {
      headers.Authorization = `Bearer ${token}`
    }

    const response = await fetch(baseUrl + path, {
      method,
      headers,
      body:
        init.jsonFile ??
        (init.body === undefined ? undefined : JSON.stringify(init.body)),
      signal: controller.signal,
    })

    if (!response.ok) {
      // Only a request that carried a token can have been signed out; login's
      // 401 is just a wrong password.
      if (
        response.status === 401 &&
        token !== null &&
        (init.unauthorized ?? 'session') === 'session'
      ) {
        sessionEndedHandler?.({
          changesData: init.changesData ?? method !== 'GET',
          tokenExpired: isTokenExpired(token),
        })
      }
      const { code, reason } = await readErrorBody(response)
      throw new ApiError(response.status, code, reason)
    }
    return await read(response)
  } finally {
    inFlight.delete(controller)
  }
}

// send() plus the JSON response body.
//
// `<T>` is a generic: the caller names the response type it expects
// (`request<AuthResponse>(...)`) and gets a Promise of that back.
export function request<T>(
  path: string,
  init: RequestOptions = {},
): Promise<T> {
  return send(path, init, async (response) => {
    // 204 has no body; response.json() on it rejects. Milestone 9's delete
    // routes return it, so handle it now rather than debug it then. 202 is the
    // same here: POST /auth/register answers it with no body (specs/002 FR-011).
    if (response.status === 204 || response.status === 202) {
      return undefined as T
    }

    // No runtime check that the JSON matches T — we own both ends of this API
    // and the response records are the contract, so a schema library would be
    // paying for a guarantee the OpenAPI document already gives. This cast is
    // that decision made explicit.
    return (await response.json()) as T
  })
}
