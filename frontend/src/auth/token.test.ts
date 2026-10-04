import { describe, expect, it } from 'vitest'
import { isTokenExpired } from './token'

// A JWT is three base64url parts; only the middle one (the claims) matters
// here, so the header and signature are placeholders.
function tokenWithClaims(claims: unknown): string {
  const payload = btoa(JSON.stringify(claims))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '')
  return `header.${payload}.signature`
}

const now = new Date('2026-10-04T09:30:00Z')
const nowSeconds = now.getTime() / 1000

describe('isTokenExpired', () => {
  it('is true once exp has passed', () => {
    const token = tokenWithClaims({ sub: '7', exp: nowSeconds - 60 })

    expect(isTokenExpired(token, now)).toBe(true)
  })

  it('is true at exactly exp', () => {
    const token = tokenWithClaims({ exp: nowSeconds })

    expect(isTokenExpired(token, now)).toBe(true)
  })

  it('is false before exp', () => {
    const token = tokenWithClaims({ exp: nowSeconds + 60 })

    expect(isTokenExpired(token, now)).toBe(false)
  })

  // Unreadable means "not expired": the stricter path, which clears the draft.
  it('is false for something that is not a JWT', () => {
    expect(isTokenExpired('not-a-token', now)).toBe(false)
    expect(isTokenExpired('a.%%%.c', now)).toBe(false)
  })

  it('is false without a numeric exp claim', () => {
    expect(isTokenExpired(tokenWithClaims({ sub: '7' }), now)).toBe(false)
    expect(isTokenExpired(tokenWithClaims({ exp: 'soon' }), now)).toBe(false)
  })
})
