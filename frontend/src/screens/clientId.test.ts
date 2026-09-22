import { describe, expect, it } from 'vitest'
import { createClientId } from './clientId'

// The stubs stand in for the two contexts the app actually runs in: an HTTPS or
// localhost origin, where crypto.randomUUID is defined, and a plain-HTTP LAN
// address, where it is not. Casting through `unknown` because neither stub is a
// whole Crypto and only this one method is under test.
const secureContextCrypto = {
  randomUUID: () => '8e29f0f4-2f7a-4f2f-9a4e-6f3f6a5d2b11',
} as unknown as Crypto

const insecureContextCrypto = {} as unknown as Crypto

describe('createClientId', () => {
  it('uses crypto.randomUUID when the secure-context method is available', () => {
    expect(createClientId(secureContextCrypto)).toBe(
      '8e29f0f4-2f7a-4f2f-9a4e-6f3f6a5d2b11',
    )
  })

  // Regression: the session editor threw here when opened over plain HTTP,
  // which the screen could only report as "could not be opened".
  it('returns an id instead of throwing when randomUUID is missing', () => {
    expect(createClientId(insecureContextCrypto)).toMatch(/^draft-/)
  })

  // Uniqueness within the open page is the entire contract these ids have to
  // meet: they are React keys and the handles the draft update helpers match on.
  it('returns a distinct id on every call without randomUUID', () => {
    const ids = new Set([
      createClientId(insecureContextCrypto),
      createClientId(insecureContextCrypto),
      createClientId(insecureContextCrypto),
    ])

    expect(ids.size).toBe(3)
  })
})
