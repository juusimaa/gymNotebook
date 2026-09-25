import { getToken } from '../auth/token'

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
// "account_suspended", a 403 at register is a wrong invite code — and `code`
// holds it when present. Extending Error is what lets a screen tell "the server
// said no" apart from "the request never got there": `err instanceof ApiError` on
// the one hand, fetch's own TypeError on the other.
export class ApiError extends Error {
  // Declared as fields rather than `constructor(public readonly status)`
  // parameter properties: tsconfig has erasableSyntaxOnly on, which only allows
  // TypeScript syntax that can be deleted to leave valid JavaScript, and
  // parameter properties generate an assignment.
  readonly status: number
  readonly code: string | undefined

  constructor(status: number, code?: string) {
    super(`API responded ${status}`)
    this.status = status
    this.code = code
  }
}

// Reads the `code` out of an error response, if it has one. Only a JSON body is
// parsed, and a body that isn't the expected shape just means "no code" — the
// status alone is still a complete error.
async function readErrorCode(response: Response): Promise<string | undefined> {
  if (!response.headers.get('Content-Type')?.includes('application/json')) {
    return undefined
  }
  try {
    const body: unknown = await response.json()
    if (
      typeof body === 'object' &&
      body !== null &&
      'code' in body &&
      typeof body.code === 'string'
    ) {
      return body.code
    }
  } catch {
    // Malformed JSON on an error response: fall back to the status alone.
  }
  return undefined
}

interface RequestOptions {
  method?: string
  // Anything JSON.stringify can serialise. `unknown` rather than `object` so a
  // caller must mean it; the wire types in api/auth.ts etc. are what give it shape.
  body?: unknown
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
  if (init.body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }

  const token = getToken()
  if (token !== null) {
    headers.Authorization = `Bearer ${token}`
  }

  // Our own controller, so abortPendingRequests() can cancel this request; the
  // caller's signal, if any, is forwarded to it.
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
    const response = await fetch(baseUrl + path, {
      method,
      headers,
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
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
        })
      }
      throw new ApiError(response.status, await readErrorCode(response))
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
    // routes return it, so handle it now rather than debug it then.
    if (response.status === 204) {
      return undefined as T
    }

    // No runtime check that the JSON matches T — we own both ends of this API
    // and the response records are the contract, so a schema library would be
    // paying for a guarantee the OpenAPI document already gives. This cast is
    // that decision made explicit.
    return (await response.json()) as T
  })
}
