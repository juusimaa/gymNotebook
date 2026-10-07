import { describe, expect, it } from 'vitest'
import { readRouteNotice, routeNotice } from './routeNotice'

describe('readRouteNotice', () => {
  it('reads the notice a screen was opened with', () => {
    expect(readRouteNotice(routeNotice('Page saved.'))).toBe('Page saved.')
  })

  // Router state survives reloads and can be anything a past version wrote.
  it('ignores state it does not recognise', () => {
    expect(readRouteNotice(null)).toBeNull()
    expect(readRouteNotice(undefined)).toBeNull()
    expect(readRouteNotice('Page saved.')).toBeNull()
    expect(readRouteNotice({ notice: 42 })).toBeNull()
    expect(readRouteNotice({ notice: '  ' })).toBeNull()
    expect(readRouteNotice({ email: 'a@example.test' })).toBeNull()
  })
})
