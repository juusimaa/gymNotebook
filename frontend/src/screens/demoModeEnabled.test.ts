import { describe, expect, it } from 'vitest'
import { resolveDemoModeEnabled } from './demoModeEnabled'

describe('resolveDemoModeEnabled', () => {
  it("is on when the deployed container's config.js says true", () => {
    expect(resolveDemoModeEnabled('true', undefined)).toBe(true)
  })

  // runtime-config.sh writes "" when DEMO_MODE_ENABLED is unset; that must not
  // hide the dev server's value.
  it('falls back to the build-time value when config.js has none', () => {
    expect(resolveDemoModeEnabled('', 'true')).toBe(true)
    expect(resolveDemoModeEnabled(undefined, 'true')).toBe(true)
  })

  it('prefers config.js over the build-time value', () => {
    expect(resolveDemoModeEnabled('false', 'true')).toBe(false)
  })

  // The API's fail-closed rule: only the exact value "true".
  it.each([undefined, '', 'false', 'True', '1', ' true'])(
    'is off for %o',
    (value) => {
      expect(resolveDemoModeEnabled(value, undefined)).toBe(false)
    },
  )
})
