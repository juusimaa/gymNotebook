import { describe, expect, it } from 'vitest'
import {
  getTokenSubject,
  isTokenExpired,
  shouldRenew,
  shouldRenewOnHide,
} from './token'

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

// specs/003 D7: renew past half the token's lifetime, never once it's expired.
describe('shouldRenew', () => {
  // A 30-minute token issued `minutesAgo` before now.
  function issued(minutesAgo: number): string {
    const iat = nowSeconds - minutesAgo * 60
    return tokenWithClaims({ sub: '7', iat, exp: iat + 30 * 60 })
  }

  it('is false in the first half of the lifetime', () => {
    expect(shouldRenew(issued(0), now)).toBe(false)
    expect(shouldRenew(issued(14), now)).toBe(false)
  })

  it('is true from half-life until expiry', () => {
    expect(shouldRenew(issued(15), now)).toBe(true)
    expect(shouldRenew(issued(29), now)).toBe(true)
  })

  it('is false once expired', () => {
    expect(shouldRenew(issued(30), now)).toBe(false)
    expect(shouldRenew(issued(45), now)).toBe(false)
  })

  // Issued before renewal existed: it runs out as tokens always did.
  it('is false without iat or exp', () => {
    expect(shouldRenew(tokenWithClaims({ exp: nowSeconds + 60 }), now)).toBe(
      false,
    )
    expect(shouldRenew(tokenWithClaims({ iat: nowSeconds - 60 }), now)).toBe(
      false,
    )
    expect(shouldRenew('not-a-token', now)).toBe(false)
  })
})

describe('getTokenSubject', () => {
  it('reads the sub claim', () => {
    expect(getTokenSubject(tokenWithClaims({ sub: '7' }))).toBe('7')
  })

  it('is null when there is no readable string sub', () => {
    expect(getTokenSubject(tokenWithClaims({ sub: 7 }))).toBeNull()
    expect(getTokenSubject(tokenWithClaims({}))).toBeNull()
    expect(getTokenSubject('not-a-token')).toBeNull()
  })
})

// specs/003 D7: hiding the tab renews any token five minutes old, never an
// expired one, so a locked phone sleeps on a nearly fresh token.
describe('shouldRenewOnHide', () => {
  // A 30-minute token issued `minutesAgo` before now.
  function issued(minutesAgo: number): string {
    const iat = nowSeconds - minutesAgo * 60
    return tokenWithClaims({ sub: '7', iat, exp: iat + 30 * 60 })
  }

  it('is false under five minutes old', () => {
    expect(shouldRenewOnHide(issued(0), now)).toBe(false)
    expect(shouldRenewOnHide(issued(4), now)).toBe(false)
  })

  it('is true from five minutes old until expiry', () => {
    expect(shouldRenewOnHide(issued(5), now)).toBe(true)
    expect(shouldRenewOnHide(issued(29), now)).toBe(true)
  })

  it('is false once expired', () => {
    expect(shouldRenewOnHide(issued(30), now)).toBe(false)
  })

  it('is false without iat or exp', () => {
    expect(
      shouldRenewOnHide(tokenWithClaims({ exp: nowSeconds + 60 }), now),
    ).toBe(false)
    expect(shouldRenewOnHide('not-a-token', now)).toBe(false)
  })
})
