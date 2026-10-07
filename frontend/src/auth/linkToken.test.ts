import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { linkFailure, readFragmentToken } from './linkToken'

describe('readFragmentToken', () => {
  it('reads the token from the fragment', () => {
    expect(readFragmentToken('#token=header.payload.signature')).toBe(
      'header.payload.signature',
    )
  })

  // EmailTemplates escapes the token with Uri.EscapeDataString.
  it('undoes percent-escaping', () => {
    expect(readFragmentToken('#token=a%2Eb%2Ec')).toBe('a.b.c')
  })

  it('accepts a hash without the leading #', () => {
    expect(readFragmentToken('token=abc')).toBe('abc')
  })

  it.each(['', '#', '#token=', '#token=%20', '#other=abc'])(
    'treats %j as no token',
    (hash) => {
      expect(readFragmentToken(hash)).toBeNull()
    },
  )
})

describe('linkFailure', () => {
  it('maps 400 expired to expired', () => {
    expect(linkFailure(new ApiError(400, 'expired'))).toBe('expired')
  })

  it.each([
    ['invalid', 'invalid'],
    ['invalid_request', 'invalid'],
    [undefined, 'invalid'],
  ])('maps 400 %s to invalid', (code, expected) => {
    expect(linkFailure(new ApiError(400, code))).toBe(expected)
  })

  // Not a verdict on the link: worth trying again.
  it.each([
    new ApiError(429),
    new ApiError(500),
    new TypeError('Failed to fetch'),
  ])('is null for %s', (error) => {
    expect(linkFailure(error)).toBeNull()
  })
})
