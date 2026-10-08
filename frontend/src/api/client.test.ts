import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getToken, setToken } from '../auth/token'
import {
  abortPendingRequests,
  ApiError,
  installBackgroundRenewal,
  onSessionEnded,
  renewTokenIfDue,
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

// Token renewal (specs/003 D7). Each test uses its own token, since the
// client remembers a refused one for the rest of the module's life.
describe('token renewal', () => {
  // A 30-minute token for user 7 issued `minutesAgo` before now; `tag` makes
  // otherwise identical tokens distinct.
  function tokenIssued(minutesAgo: number, tag: string): string {
    const iat = Math.floor(Date.now() / 1000) - minutesAgo * 60
    const claims = { sub: '7', iat, exp: iat + 30 * 60, tag }
    const payload = btoa(JSON.stringify(claims))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '')
    return `header.${payload}.signature`
  }

  // fetch that answers POST /auth/token with `renewal` and everything else 200,
  // recording the bearer token each request carried.
  function serverRenewingWith(renewal: () => Promise<Response>) {
    const calls: { path: string; token: string | undefined }[] = []
    const fetchMock = vi.fn((url: string, init: RequestInit) => {
      const headers = init.headers as Record<string, string>
      calls.push({
        path: new URL(url).pathname,
        token: headers.Authorization?.replace('Bearer ', ''),
      })
      return url.endsWith('/auth/token')
        ? renewal()
        : Promise.resolve(Response.json({ ok: true }))
    })
    vi.stubGlobal('fetch', fetchMock)
    return calls
  }

  const renewedTo = (token: string) => () =>
    Promise.resolve(Response.json({ token }))

  it('leaves a token in its first half-life alone', async () => {
    const token = tokenIssued(5, 'fresh')
    setToken(token)
    const calls = serverRenewingWith(renewedTo('unused'))

    await request('/workouts')

    expect(calls).toEqual([{ path: '/workouts', token }])
  })

  it('renews a token past half-life before the request, which carries the new one', async () => {
    const old = tokenIssued(20, 'old')
    setToken(old)
    const calls = serverRenewingWith(renewedTo('renewed'))

    await request('/workouts')

    expect(calls).toEqual([
      { path: '/auth/token', token: old },
      { path: '/workouts', token: 'renewed' },
    ])
    expect(getToken()).toBe('renewed')
  })

  it('shares one renewal between requests sent together', async () => {
    setToken(tokenIssued(20, 'shared'))
    const calls = serverRenewingWith(renewedTo('renewed-once'))

    await Promise.all([request('/workouts'), request('/exercises')])

    expect(calls.filter((c) => c.path === '/auth/token')).toHaveLength(1)
    expect(calls.filter((c) => c.token === 'renewed-once')).toHaveLength(2)
  })

  it('stops renewing a token the server refused past the cap', async () => {
    const capped = tokenIssued(20, 'capped')
    setToken(capped)
    const calls = serverRenewingWith(() =>
      Promise.resolve(
        Response.json({ code: 'renewal_refused' }, { status: 403 }),
      ),
    )

    await request('/workouts')
    await request('/workouts')

    // The token still works until it expires; it is asked about only once.
    expect(calls).toEqual([
      { path: '/auth/token', token: capped },
      { path: '/workouts', token: capped },
      { path: '/workouts', token: capped },
    ])
    expect(ended).toEqual([])
  })

  it('lets the request go ahead when renewal fails', async () => {
    const token = tokenIssued(20, 'offline')
    setToken(token)
    const calls = serverRenewingWith(() =>
      Promise.reject(new TypeError('Failed to fetch')),
    )

    await request('/workouts')

    expect(calls.at(-1)).toEqual({ path: '/workouts', token })
    expect(getToken()).toBe(token)
  })

  // The renewal's own 401 never ends the session; the request's 401 does.
  it('leaves a 401 on the renewal to the request that follows', async () => {
    setToken(tokenIssued(20, 'revoked'))
    serverRenewingWith(() =>
      Promise.resolve(new Response(null, { status: 401 })),
    )

    await request('/workouts')

    expect(ended).toEqual([])
  })

  it('does not overwrite a token that changed while renewing', async () => {
    setToken(tokenIssued(20, 'replaced'))
    serverRenewingWith(() => {
      // Another tab signs in as someone else meanwhile.
      setToken('someone-else')
      return Promise.resolve(Response.json({ token: 'renewed' }))
    })

    await request('/workouts')

    expect(getToken()).toBe('someone-else')
  })

  describe('installBackgroundRenewal', () => {
    // A document whose visibility the test flips, and timers whose one interval
    // the test runs by hand.
    function fakeDocument(initial: DocumentVisibilityState) {
      const listeners = new Map<string, () => void>()
      const doc = {
        visibilityState: initial,
        addEventListener: (type: string, listener: () => void) =>
          listeners.set(type, listener),
        removeEventListener: (type: string) => listeners.delete(type),
        show: () => {
          doc.visibilityState = 'visible'
          listeners.get('visibilitychange')?.()
        },
        hide: () => {
          doc.visibilityState = 'hidden'
          listeners.get('visibilitychange')?.()
        },
        has: () => listeners.has('visibilitychange'),
      }
      return doc
    }

    function fakeTimers() {
      const timers = {
        tick: undefined as (() => void) | undefined,
        every: undefined as number | undefined,
        setInterval: (callback: () => void, ms: number) => {
          timers.tick = callback
          timers.every = ms
          return 1
        },
        clearInterval: () => {
          timers.tick = undefined
        },
      }
      return timers
    }

    function install(
      doc: ReturnType<typeof fakeDocument>,
      timers: ReturnType<typeof fakeTimers>,
    ) {
      return installBackgroundRenewal(
        doc as unknown as Document,
        timers as unknown as Pick<
          typeof globalThis,
          'setInterval' | 'clearInterval'
        >,
      )
    }

    // Whether the renewal POST asked to outlive the page.
    const keptAlive = () =>
      vi
        .mocked(fetch)
        .mock.calls.map(([, init]) => (init as RequestInit).keepalive)

    it('renews when the tab becomes visible, until uninstalled', async () => {
      setToken(tokenIssued(20, 'visible'))
      const calls = serverRenewingWith(renewedTo('renewed-on-visible'))
      const doc = fakeDocument('hidden')
      const timers = fakeTimers()
      const uninstall = install(doc, timers)

      doc.show()
      await renewTokenIfDue()

      expect(calls.map((c) => c.path)).toEqual(['/auth/token'])
      expect(getToken()).toBe('renewed-on-visible')
      uninstall()
      expect(doc.has()).toBe(false)
      expect(timers.tick).toBeUndefined()
    })

    // A screen left open with nothing to save would otherwise run out.
    it('checks every minute while visible and renews past half-life', async () => {
      const doc = fakeDocument('visible')
      const timers = fakeTimers()
      const uninstall = install(doc, timers)
      setToken(tokenIssued(20, 'open-screen'))
      const calls = serverRenewingWith(renewedTo('renewed-on-check'))

      timers.tick?.()
      await renewTokenIfDue()

      expect(timers.every).toBe(60_000)
      expect(calls.map((c) => c.path)).toEqual(['/auth/token'])
      expect(getToken()).toBe('renewed-on-check')
      uninstall()
    })

    it('leaves a token in its first half-life alone on the minute check', () => {
      const doc = fakeDocument('visible')
      const timers = fakeTimers()
      const uninstall = install(doc, timers)
      setToken(tokenIssued(10, 'checked-young'))
      const calls = serverRenewingWith(renewedTo('unused'))

      timers.tick?.()

      expect(calls).toEqual([])
      uninstall()
    })

    // A phone locked between exercises sleeps on a nearly fresh token.
    it('stops checking when hidden and renews a token five minutes old, kept alive', async () => {
      const doc = fakeDocument('visible')
      const timers = fakeTimers()
      const uninstall = install(doc, timers)
      setToken(tokenIssued(6, 'locked'))
      const calls = serverRenewingWith(renewedTo('renewed-on-hide'))

      doc.hide()
      await vi.waitFor(() => expect(getToken()).toBe('renewed-on-hide'))

      expect(timers.tick).toBeUndefined()
      expect(calls.map((c) => c.path)).toEqual(['/auth/token'])
      expect(keptAlive()).toEqual([true])
      uninstall()
    })

    // The editor flushes unsaved sets on the same event; that save goes first.
    it('does not hold back a request sent while the hide renewal is pending', async () => {
      const doc = fakeDocument('visible')
      const uninstall = install(doc, fakeTimers())
      const token = tokenIssued(6, 'flush-on-hide')
      setToken(token)
      // The page freezes before the renewal answers.
      const calls = serverRenewingWith(() => new Promise<Response>(() => {}))

      doc.hide()
      await request('/workouts')

      expect(calls).toEqual([
        { path: '/auth/token', token },
        { path: '/workouts', token },
      ])
      uninstall()
      abortPendingRequests()
    })

    // Switching tabs back and forth mustn't spend the auth rate limit.
    it('leaves a token under five minutes old alone when hidden', () => {
      const doc = fakeDocument('visible')
      const timers = fakeTimers()
      const uninstall = install(doc, timers)
      setToken(tokenIssued(4, 'hidden-young'))
      const calls = serverRenewingWith(renewedTo('unused'))

      doc.hide()

      expect(calls).toEqual([])
      uninstall()
    })

    it('does not check while installed on a hidden tab', () => {
      const timers = fakeTimers()
      const uninstall = install(fakeDocument('hidden'), timers)

      expect(timers.tick).toBeUndefined()
      uninstall()
    })
  })
})
