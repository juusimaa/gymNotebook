import { describe, expect, it } from 'vitest'
import { resolveTurnstileSiteKey } from './turnstileSiteKey'

describe('resolveTurnstileSiteKey', () => {
  it("prefers the deployed container's config.js", () => {
    expect(resolveTurnstileSiteKey('runtime-key', 'build-key')).toBe(
      'runtime-key',
    )
  })

  // runtime-config.sh writes "" when TURNSTILE_SITE_KEY is unset; that must not
  // hide the dev server's value.
  it('falls back to the build-time value when config.js has none', () => {
    expect(resolveTurnstileSiteKey('', 'build-key')).toBe('build-key')
    expect(resolveTurnstileSiteKey(undefined, 'build-key')).toBe('build-key')
  })

  it('is empty, switching the check off, when neither is set', () => {
    expect(resolveTurnstileSiteKey(undefined, undefined)).toBe('')
    expect(resolveTurnstileSiteKey('', '  ')).toBe('')
  })
})
