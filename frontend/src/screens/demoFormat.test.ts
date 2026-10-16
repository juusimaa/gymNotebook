import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import {
  describeDemoResetError,
  describeDemoStartError,
  formatDemoEnd,
} from './demoFormat'

describe('formatDemoEnd', () => {
  // Helsinki is UTC+3 in October.
  const zone = 'Europe/Helsinki'

  it('says today for an end later the same local day', () => {
    expect(
      formatDemoEnd(
        '2026-10-16T18:40:00Z',
        new Date('2026-10-16T05:00:00Z'),
        zone,
      ),
    ).toBe('today at 21.40')
  })

  it('says tomorrow for an end on the next local day', () => {
    expect(
      formatDemoEnd(
        '2026-10-17T15:30:00Z',
        new Date('2026-10-16T15:30:00Z'),
        zone,
      ),
    ).toBe('tomorrow at 18.30')
  })

  // 22.30 UTC is already the 17th in Helsinki: the day is the local one.
  it('compares local days, not UTC days', () => {
    expect(
      formatDemoEnd(
        '2026-10-16T22:30:00Z',
        new Date('2026-10-16T20:00:00Z'),
        zone,
      ),
    ).toBe('tomorrow at 01.30')
  })

  it('falls back to the date when the device clock is far off', () => {
    expect(
      formatDemoEnd(
        '2026-10-17T15:30:00Z',
        new Date('2026-10-01T12:00:00Z'),
        zone,
      ),
    ).toBe('on 17 October 2026 at 18.30')
  })
})

describe('describeDemoStartError', () => {
  it.each([
    [new ApiError(400, 'captcha'), "The check didn't pass. Try again."],
    [
      new ApiError(503, 'demo_unavailable'),
      'Too many demos are running right now. Try again in a few minutes.',
    ],
    [
      new ApiError(429),
      'Too many demos started from this network. Try again later.',
    ],
    [new ApiError(404), "The demo isn't available right now."],
    [
      new ApiError(400, 'invalid_request'),
      "The demo couldn't start. Reload the page and try again.",
    ],
    [new ApiError(500), 'The server answered 500'],
    [
      new TypeError('Failed to fetch'),
      "Can't reach the notebook. Check your connection and try again.",
    ],
  ])('maps %o to its message', (err, message) => {
    expect(describeDemoStartError(err)).toBe(message)
  })
})

describe('describeDemoResetError', () => {
  it.each([
    [
      new ApiError(503, 'temporarily_unavailable'),
      'The notebook is busy, so nothing was reset. Try again in a moment.',
    ],
    [new ApiError(429), 'Too many resets. Wait a minute and try again.'],
    [
      new ApiError(403, 'not_a_demo'),
      "This account isn't a demo, so nothing was reset.",
    ],
    [
      new TypeError('Failed to fetch'),
      'The reset may not have finished. Open the notebook to check, or try again.',
    ],
  ])('maps %o to its message', (err, message) => {
    expect(describeDemoResetError(err)).toBe(message)
  })
})
