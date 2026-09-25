import { describe, expect, it } from 'vitest'
import type { AccountPrivacyState } from '../api/privacy'
import {
  DEFAULT_NOTEBOOK_PATH,
  isNotebookPath,
  needsNoticeGate,
  noticeGateUrl,
  safeReturnPath,
} from './noticeGate'

function state(requiresAcknowledgement: boolean): AccountPrivacyState {
  return {
    currentNoticeVersion: 'v2',
    acknowledgement: requiresAcknowledgement
      ? null
      : { noticeVersion: 'v2', acknowledgedAt: '2026-09-25T10:00:00Z' },
    requiresAcknowledgement,
  }
}

describe('needsNoticeGate', () => {
  it('gates when the server says acknowledgement is required', () => {
    expect(needsNoticeGate(state(true))).toBe(true)
  })

  it('lets the notebook open once the current version is acknowledged', () => {
    expect(needsNoticeGate(state(false))).toBe(false)
  })

  // null is GET /account/privacy's 404: the feature is off, so no gate.
  it('never gates when the feature is off', () => {
    expect(needsNoticeGate(null)).toBe(false)
  })
})

describe('isNotebookPath', () => {
  it.each([
    '/workouts',
    '/workouts/new',
    '/workouts/12',
    '/workouts/12/edit',
    '/progress',
    '/exercises',
    '/exercises/3',
  ])('treats %s as notebook content', (path) => {
    expect(isNotebookPath(path)).toBe(true)
  })

  // Reachable without acknowledging (contracts/ui.md), and look-alikes.
  it.each([
    '/',
    '/change-password',
    '/account/privacy',
    '/account/privacy/notice',
    '/privacy',
    '/workoutsx',
    '/login',
  ])('does not treat %s as notebook content', (path) => {
    expect(isNotebookPath(path)).toBe(false)
  })
})

describe('safeReturnPath', () => {
  it('keeps a notebook path with its query and hash', () => {
    expect(safeReturnPath('/workouts/12?tab=sets#top')).toBe(
      '/workouts/12?tab=sets#top',
    )
  })

  it('falls back to the default when there is no return path', () => {
    expect(safeReturnPath(null)).toBe(DEFAULT_NOTEBOOK_PATH)
    expect(safeReturnPath('')).toBe(DEFAULT_NOTEBOOK_PATH)
  })

  // Open-redirect attempts: each would leave the site if navigated to as-is.
  it.each([
    'https://evil.example/workouts',
    '//evil.example/workouts',
    '/\\evil.example/workouts',
    '/\t/evil.example/workouts',
    'javascript:alert(1)',
    'workouts',
  ])('rejects %j', (raw) => {
    expect(safeReturnPath(raw)).toBe(DEFAULT_NOTEBOOK_PATH)
  })

  // Same origin but not notebook content: nothing to return to past the gate.
  it.each(['/account/privacy/notice', '/login', '/'])(
    'rejects the non-notebook path %s',
    (raw) => {
      expect(safeReturnPath(raw)).toBe(DEFAULT_NOTEBOOK_PATH)
    },
  )

  // The parser resolves dot segments, so a path that only *starts* like a
  // notebook route can't climb out of it.
  it('judges the normalised path, not the raw text', () => {
    expect(safeReturnPath('/workouts/../account/privacy')).toBe(
      DEFAULT_NOTEBOOK_PATH,
    )
  })

  it('round-trips through noticeGateUrl', () => {
    const url = new URL(noticeGateUrl('/workouts/12?x=1&y=2'), 'https://a.b')
    expect(safeReturnPath(url.searchParams.get('returnTo'))).toBe(
      '/workouts/12?x=1&y=2',
    )
  })
})
