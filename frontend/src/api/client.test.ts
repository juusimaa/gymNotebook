import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { setToken } from '../auth/token'
import {
  abortPendingRequests,
  ApiError,
  onSessionEnded,
  request,
  type SessionEnded,
} from './client'

// The client's part in session invalidation (specs/001 contracts/ui.md): which
// 401s end the session, and that a sign-out can abort requests in flight.
// fetch and localStorage are stubbed; nothing touches the network.

let ended: SessionEnded[]

beforeEach(() => {
  const store = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => store.set(key, value),
    removeItem: (key: string) => store.delete(key),
  })
  ended = []
  onSessionEnded((event) => ended.push(event))
})

afterEach(() => {
  onSessionEnded(null)
  vi.unstubAllGlobals()
})

function respondWith(status: number, body?: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(body === undefined ? null : JSON.stringify(body), {
          status,
          headers:
            body === undefined ? {} : { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  )
}

async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => {
      throw new Error('expected the request to fail')
    },
    (err: unknown) => err,
  )
}

describe('401 handling', () => {
  it('reports a 401 on a signed-in read as a session end', async () => {
    setToken('revoked')
    respondWith(401)

    const err = await rejectionOf(request('/workouts'))

    expect(err).toBeInstanceOf(ApiError)
    expect(ended).toEqual([{ changesData: false, tokenExpired: false }])
  })

  it('marks a 401 on a write as possibly saved', async () => {
    setToken('revoked')
    respondWith(401)

    await rejectionOf(request('/workouts', { method: 'POST', body: {} }))

    expect(ended).toEqual([{ changesData: true, tokenExpired: false }])
  })

  it('lets a read-only POST say it changed nothing', async () => {
    setToken('revoked')
    respondWith(401)

    await rejectionOf(
      request('/account/export', {
        method: 'POST',
        body: {},
        changesData: false,
      }),
    )

    expect(ended).toEqual([{ changesData: false, tokenExpired: false }])
  })

  // A token whose own exp has passed: invalidation holds the editor draft for
  // the next sign-in instead of clearing it.
  it('says when the rejected token had simply expired', async () => {
    const claims = btoa(JSON.stringify({ exp: Date.now() / 1000 - 60 }))
    setToken(`header.${claims.replace(/=+$/, '')}.signature`)
    respondWith(401)

    await rejectionOf(request('/workouts', { method: 'PUT', body: {} }))

    expect(ended).toEqual([{ changesData: true, tokenExpired: true }])
  })

  it('leaves a 401 to the caller when it handles it locally', async () => {
    setToken('valid')
    respondWith(401)

    await rejectionOf(
      request('/auth/change-password', {
        method: 'POST',
        body: {},
        unauthorized: 'local',
      }),
    )

    expect(ended).toEqual([])
  })

  // Login's 401 is a wrong password, not a sign-out.
  it('ignores a 401 on a request sent without a token', async () => {
    respondWith(401)

    await rejectionOf(request('/auth/login', { method: 'POST', body: {} }))

    expect(ended).toEqual([])
  })

  // A wrong password on export or deletion stays with the form.
  it('does not end the session on 400 password_verification_failed', async () => {
    setToken('valid')
    respondWith(400, { code: 'password_verification_failed' })

    const err = await rejectionOf(
      request('/account/delete', { method: 'POST', body: {} }),
    )

    expect(err).toMatchObject({
      status: 400,
      code: 'password_verification_failed',
    })
    expect(ended).toEqual([])
  })
})

describe('abortPendingRequests', () => {
  it('aborts a request still in flight', async () => {
    // A fetch that only ends when its signal aborts.
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (_url: string, init: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init.signal?.addEventListener('abort', () =>
              reject(new DOMException('Aborted', 'AbortError')),
            )
          }),
      ),
    )
    const pending = rejectionOf(request('/workouts'))

    abortPendingRequests()

    expect(await pending).toMatchObject({ name: 'AbortError' })
  })

  it("still honours the caller's own signal", async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (_url: string, init: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init.signal?.addEventListener('abort', () =>
              reject(new DOMException('Aborted', 'AbortError')),
            )
          }),
      ),
    )
    const controller = new AbortController()
    const pending = rejectionOf(
      request('/workouts', { signal: controller.signal }),
    )

    controller.abort()

    expect(await pending).toMatchObject({ name: 'AbortError' })
  })
})

// POST /auth/register answers 202 and the resend route 204, both with no body;
// reading JSON from either would reject even though the call succeeded.
describe('bodiless success', () => {
  it.each([202, 204])(
    'resolves a %i with no body to undefined',
    async (status) => {
      respondWith(status)

      await expect(
        request('/auth/register', { method: 'POST', body: {} }),
      ).resolves.toBeUndefined()
    },
  )
})

// The restore route (specs/004 contracts/api.md): the backup file goes out
// byte for byte, and a rejection's `reason` reaches the screen.
describe('restore requests', () => {
  it('sends a JSON file unchanged, as JSON', async () => {
    respondWith(200, {})
    const file = new Blob(['{"formatVersion":1}'])

    await request('/account/restore', { method: 'POST', jsonFile: file })

    const [, init] = vi.mocked(fetch).mock.calls[0]
    expect(init?.body).toBe(file)
    expect(init?.headers).toMatchObject({ 'Content-Type': 'application/json' })
  })

  it('reads the reason beside the code of an error body', async () => {
    respondWith(400, { code: 'backup_invalid', reason: 'unsupported_version' })

    const err = await rejectionOf(
      request('/account/restore', { method: 'POST', body: {} }),
    )

    expect(err).toMatchObject({
      status: 400,
      code: 'backup_invalid',
      reason: 'unsupported_version',
    })
  })
})
