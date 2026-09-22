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

// A non-2xx response. The backend answers errors with a bare status and no body
// (Results.BadRequest(), Results.Conflict(), ...), so the status is the whole
// error. Extending Error is what lets a screen tell "the server said no" apart from
// "the request never got there": `err instanceof ApiError` on the one hand, fetch's
// own TypeError on the other.
export class ApiError extends Error {
  // Declared as a field rather than a `constructor(public readonly status)`
  // parameter property: tsconfig has erasableSyntaxOnly on, which only allows
  // TypeScript syntax that can be deleted to leave valid JavaScript, and
  // parameter properties generate an assignment.
  readonly status: number

  constructor(status: number) {
    super(`API responded ${status}`)
    this.status = status
  }
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
    throw new ApiError(response.status)
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
