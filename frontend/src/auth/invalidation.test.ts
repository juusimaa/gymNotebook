import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest'
import {
  clearInterruptedWrite,
  endSession,
  endSessionHere,
  handlePageShow,
  handleTokenStorageChange,
  hasInterruptedWrite,
  installInvalidation,
  type InvalidationEnvironment,
} from './invalidation'
import { TOKEN_KEY } from './token'

// Session invalidation (specs/001 contracts/ui.md → Invalidation), with every
// browser effect replaced by a vi.fn(), so each test sees exactly what an
// ending session clears. Vitest runs in Node: no window, no router.

// Plain function properties rather than the interface's method signatures, so
// `expect(env.clearToken)` doesn't detach a method from its object.
type FakeEnvironment = Record<keyof InvalidationEnvironment, Mock<() => void>>

function fakeEnvironment(): FakeEnvironment {
  return {
    clearToken: vi.fn(),
    abortPendingRequests: vi.fn(),
    revokeDownloads: vi.fn(),
    clearDrafts: vi.fn(),
    holdDrafts: vi.fn(),
    showSignedOut: vi.fn(),
    reload: vi.fn(),
  }
}

// A stand-in for window: records listeners so a test can fire events at them.
function fakeWindow() {
  const listeners = new Map<string, (event: unknown) => void>()
  return {
    addEventListener: vi.fn(
      (type: string, listener: (event: unknown) => void) => {
        listeners.set(type, listener)
      },
    ),
    removeEventListener: vi.fn((type: string) => {
      listeners.delete(type)
    }),
    fire: (type: string, event: unknown) => listeners.get(type)?.(event),
    has: (type: string) => listeners.has(type),
  }
}

// A JWT whose only readable part is its claims: `sub` and a distinguishing `iat`.
function tokenFor(sub: string, iat = 0): string {
  const payload = btoa(JSON.stringify({ sub, iat }))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '')
  return `header.${payload}.signature`
}

beforeEach(() => {
  clearInterruptedWrite()
})

describe('endSession', () => {
  it('clears the token, aborts pending requests, revokes downloads and shows sign-in', () => {
    const env = fakeEnvironment()

    endSession(env)

    expect(env.clearToken).toHaveBeenCalledOnce()
    expect(env.abortPendingRequests).toHaveBeenCalledOnce()
    expect(env.revokeDownloads).toHaveBeenCalledOnce()
    expect(env.showSignedOut).toHaveBeenCalledOnce()
    expect(hasInterruptedWrite()).toBe(false)
  })

  // Q5: a 401 on a write may follow a commit, so sign-in warns before a retry.
  it('remembers that a write was interrupted, until cleared', () => {
    endSession(fakeEnvironment(), { changesData: true })
    expect(hasInterruptedWrite()).toBe(true)

    clearInterruptedWrite()
    expect(hasInterruptedWrite()).toBe(false)
  })

  // Editor drafts (screens/editorDraftStorage.ts): cleared with everything
  // else, unless the token had merely expired.
  it('clears the editor drafts when the session ends', () => {
    const env = fakeEnvironment()

    endSession(env)

    expect(env.clearDrafts).toHaveBeenCalledOnce()
    expect(env.holdDrafts).not.toHaveBeenCalled()
  })

  it('holds the editor drafts for the next sign-in when the token expired', () => {
    const env = fakeEnvironment()

    endSession(env, { tokenExpired: true })

    expect(env.holdDrafts).toHaveBeenCalledOnce()
    expect(env.clearDrafts).not.toHaveBeenCalled()
    expect(env.clearToken).toHaveBeenCalledOnce()
    expect(env.showSignedOut).toHaveBeenCalledOnce()
  })

  it('clears everything but stays on the screen when asked to', () => {
    const env = fakeEnvironment()

    endSession(env, { showSignedOut: false })

    expect(env.clearToken).toHaveBeenCalledOnce()
    expect(env.abortPendingRequests).toHaveBeenCalledOnce()
    expect(env.revokeDownloads).toHaveBeenCalledOnce()
    expect(env.showSignedOut).not.toHaveBeenCalled()
  })
})

describe('handleTokenStorageChange (another tab)', () => {
  it('ends the session when another tab removed the token', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange(
      { key: TOKEN_KEY, oldValue: 'a-token', newValue: null },
      env,
    )

    expect(env.clearToken).toHaveBeenCalledOnce()
    expect(env.abortPendingRequests).toHaveBeenCalledOnce()
    expect(env.showSignedOut).toHaveBeenCalledOnce()
    // A deliberate sign-out elsewhere is never an expiry.
    expect(env.clearDrafts).toHaveBeenCalledOnce()
  })

  // key null is localStorage.clear(), which removes the token too.
  it('ends the session when another tab cleared all storage', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange({ key: null, oldValue: null, newValue: null }, env)

    expect(env.showSignedOut).toHaveBeenCalledOnce()
  })

  it('reloads when another tab signed in where this one was signed out', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange(
      { key: TOKEN_KEY, oldValue: null, newValue: tokenFor('7') },
      env,
    )

    expect(env.reload).toHaveBeenCalledOnce()
    expect(env.clearToken).not.toHaveBeenCalled()
  })

  it('reloads when another tab signed in as someone else', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange(
      { key: TOKEN_KEY, oldValue: tokenFor('7'), newValue: tokenFor('8') },
      env,
    )

    expect(env.reload).toHaveBeenCalledOnce()
  })

  // specs/003 D8: a renewal elsewhere must not reload a page being edited.
  it('does nothing when another tab renewed the same user’s token', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange(
      {
        key: TOKEN_KEY,
        oldValue: tokenFor('7', 1000),
        newValue: tokenFor('7', 2000),
      },
      env,
    )

    expect(Object.values(env).every((fn) => fn.mock.calls.length === 0)).toBe(
      true,
    )
  })

  // Unreadable tokens can't prove they belong to the same user.
  it('reloads when the user of either token cannot be read', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange(
      { key: TOKEN_KEY, oldValue: 'not-a-jwt', newValue: 'not-a-jwt-either' },
      env,
    )

    expect(env.reload).toHaveBeenCalledOnce()
  })

  it('ignores changes to other keys', () => {
    const env = fakeEnvironment()

    handleTokenStorageChange(
      { key: 'something-else', oldValue: 'x', newValue: null },
      env,
    )

    expect(Object.values(env).every((fn) => fn.mock.calls.length === 0)).toBe(
      true,
    )
  })
})

describe('handlePageShow', () => {
  // A back/forward-cache restore shows the page as it was left, personal state
  // included; reloading reruns the route guards before anything renders.
  it('reloads a page restored from the back/forward cache', () => {
    const env = fakeEnvironment()

    handlePageShow({ persisted: true }, env)

    expect(env.reload).toHaveBeenCalledOnce()
  })

  it('does nothing on a normal load', () => {
    const env = fakeEnvironment()

    handlePageShow({ persisted: false }, env)

    expect(env.reload).not.toHaveBeenCalled()
  })
})

describe('installInvalidation', () => {
  let uninstall: (() => void) | undefined

  afterEach(() => {
    uninstall?.()
    uninstall = undefined
  })

  it('listens for other tabs and back/forward restores until uninstalled', () => {
    const env = fakeEnvironment()
    const target = fakeWindow()
    uninstall = installInvalidation(env, target)

    target.fire('storage', { key: TOKEN_KEY, newValue: null })
    target.fire('pageshow', { persisted: true })

    expect(env.showSignedOut).toHaveBeenCalledOnce()
    expect(env.reload).toHaveBeenCalledOnce()

    uninstall()
    uninstall = undefined
    expect(target.has('storage')).toBe(false)
    expect(target.has('pageshow')).toBe(false)
  })

  it('lets a screen end the session without navigating', () => {
    const env = fakeEnvironment()
    uninstall = installInvalidation(env, fakeWindow())

    endSessionHere()

    expect(env.clearToken).toHaveBeenCalledOnce()
    expect(env.showSignedOut).not.toHaveBeenCalled()
  })

  it('refuses to end the session before it is installed', () => {
    expect(() => endSessionHere()).toThrow()
  })
})
