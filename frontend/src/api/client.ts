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
}

// The one fetch wrapper every api/*.ts file goes through: base URL, JSON in and
// out, bearer token when there is one, non-2xx turned into a thrown ApiError.
// Deliberately thin — no retries, no timeout, no global 401 handling (that's the
// route guard's job in PR 4), so each of those stays a decision made in one place.
//
// `<T>` is a generic: the caller names the response type it expects
// (`request<AuthResponse>(...)`) and gets a Promise of that back.
export async function request<T>(
  path: string,
  init: RequestOptions = {},
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

  const response = await fetch(baseUrl + path, {
    method: init.method ?? 'GET',
    headers,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  })

  if (!response.ok) {
    throw new ApiError(response.status, await readErrorCode(response))
  }

  // 204 has no body; response.json() on it rejects. Milestone 9's delete routes
  // return it, so handle it now rather than debug it then.
  if (response.status === 204) {
    return undefined as T
  }

  // No runtime check that the JSON matches T — we own both ends of this API and
  // the response records are the contract, so a schema library would be paying
  // for a guarantee the OpenAPI document already gives. This cast is that
  // decision made explicit.
  return (await response.json()) as T
}
