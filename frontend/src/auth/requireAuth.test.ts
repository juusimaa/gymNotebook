import { beforeEach, describe, expect, it, vi } from 'vitest'
import { me } from '../api/auth'
import { ApiError } from '../api/client'
import { requireAuth } from './requireAuth'
import { getToken, setToken } from './token'

// The guard's three outcomes, without a network or a browser. Two things are
// faked:
//
// vi.mock replaces the whole '../api/auth' module with the factory's return value
// before anything imports it (Vitest hoists the call above the imports), so the
// `me` imported above *is* the vi.fn() — each test decides what it resolves or
// rejects with. Nothing here goes near fetch.
//
// Vitest runs in Node, which has no localStorage. token.ts only needs the three
// methods, so a Map behind them is enough — no jsdom. vi.stubGlobal puts it on
// globalThis, and beforeEach installs a fresh one so a token set in one test can't
// leak into the next.
vi.mock('../api/auth', () => ({ me: vi.fn() }))

beforeEach(() => {
  const store = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => store.set(key, value),
    removeItem: (key: string) => store.delete(key),
  })
  vi.mocked(me).mockReset()
})

// redirect() is a thrown Response, so the loader's "go to /login" shows up here as
// a rejection carrying a 302 with a Location header — that's what the router reads.
async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => {
      throw new Error('expected the loader to throw')
    },
    (err: unknown) => err,
  )
}

function expectRedirectToLogin(err: unknown) {
  expect(err).toBeInstanceOf(Response)
  const response = err as Response
  expect(response.status).toBe(302)
  expect(response.headers.get('Location')).toBe('/login')
}

describe('requireAuth', () => {
  it('redirects to /login without a request when there is no token', async () => {
    expectRedirectToLogin(await rejectionOf(requireAuth()))
    expect(me).not.toHaveBeenCalled()
  })

  // Expired token or stale token_version — /auth/me answers 401 for both. The
  // token is dropped so the next visit skips straight to the no-token branch.
  it('clears the token and redirects to /login when /auth/me answers 401', async () => {
    setToken('stale')
    vi.mocked(me).mockRejectedValue(new ApiError(401))

    expectRedirectToLogin(await rejectionOf(requireAuth()))
    expect(getToken()).toBeNull()
  })

  it('returns the user when the token is good', async () => {
    setToken('good')
    const user = { userId: 1, username: 'jouni' }
    vi.mocked(me).mockResolvedValue(user)

    await expect(requireAuth()).resolves.toEqual(user)
    expect(getToken()).toBe('good')
  })

  // Anything that isn't a 401 is an error, not a sign-out: it propagates to the
  // router's error boundary, and the token is left alone.
  it('rethrows other failures and keeps the token', async () => {
    setToken('good')
    const failure = new ApiError(500)
    vi.mocked(me).mockRejectedValue(failure)

    await expect(requireAuth()).rejects.toBe(failure)
    expect(getToken()).toBe('good')
  })
})
