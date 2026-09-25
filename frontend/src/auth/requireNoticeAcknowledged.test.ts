import type { LoaderFunctionArgs } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { getAccountPrivacy } from '../api/privacy'
import { requireNoticeAcknowledged } from './requireNoticeAcknowledged'
import { getToken, setToken } from './token'

// The notebook gate loader's outcomes, faked the same way as requireAuth.test.ts:
// the API module is replaced by vi.fn()s and localStorage by a Map.
vi.mock('../api/privacy', () => ({ getAccountPrivacy: vi.fn() }))

beforeEach(() => {
  const store = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => store.set(key, value),
    removeItem: (key: string) => store.delete(key),
  })
  vi.mocked(getAccountPrivacy).mockReset()
})

// Only `request` is read, so the rest of the loader arguments are left out.
function load(path: string): Promise<null> {
  const args = {
    request: new Request(`http://localhost${path}`),
  } as LoaderFunctionArgs
  return requireNoticeAcknowledged(args)
}

async function redirectLocation(promise: Promise<unknown>): Promise<string> {
  const err = await promise.then(
    () => {
      throw new Error('expected the loader to redirect')
    },
    (e: unknown) => e,
  )
  expect(err).toBeInstanceOf(Response)
  return (err as Response).headers.get('Location') ?? ''
}

describe('requireNoticeAcknowledged', () => {
  it('redirects to /login without a request when there is no token', async () => {
    expect(await redirectLocation(load('/workouts'))).toBe('/login')
    expect(getAccountPrivacy).not.toHaveBeenCalled()
  })

  it('sends an unacknowledged account to the gate, remembering the deep link', async () => {
    setToken('good')
    vi.mocked(getAccountPrivacy).mockResolvedValue({
      currentNoticeVersion: 'v1',
      acknowledgement: null,
      requiresAcknowledgement: true,
    })

    expect(await redirectLocation(load('/workouts/12?x=1'))).toBe(
      '/account/privacy/notice?returnTo=%2Fworkouts%2F12%3Fx%3D1',
    )
  })

  it('opens the notebook once the current version is acknowledged', async () => {
    setToken('good')
    vi.mocked(getAccountPrivacy).mockResolvedValue({
      currentNoticeVersion: 'v1',
      acknowledgement: {
        noticeVersion: 'v1',
        acknowledgedAt: '2026-09-25T10:00:00Z',
      },
      requiresAcknowledgement: false,
    })

    await expect(load('/progress')).resolves.toBeNull()
  })

  // getAccountPrivacy turns the unmapped route's 404 into null.
  it('opens the notebook when the feature is off', async () => {
    setToken('good')
    vi.mocked(getAccountPrivacy).mockResolvedValue(null)

    await expect(load('/exercises')).resolves.toBeNull()
  })

  it('clears the token and redirects to /login on 401', async () => {
    setToken('stale')
    vi.mocked(getAccountPrivacy).mockRejectedValue(new ApiError(401))

    expect(await redirectLocation(load('/workouts'))).toBe('/login')
    expect(getToken()).toBeNull()
  })

  // Failing closed: the router's error screen, never the notebook.
  it('rethrows other failures and keeps the token', async () => {
    setToken('good')
    const failure = new TypeError('Failed to fetch')
    vi.mocked(getAccountPrivacy).mockRejectedValue(failure)

    await expect(load('/workouts')).rejects.toBe(failure)
    expect(getToken()).toBe('good')
  })
})
