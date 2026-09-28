import { describe, expect, it } from 'vitest'
import type { AccountPrivacyState } from '../api/privacy'
import {
  DEFAULT_NOTEBOOK_PATH,
  isNotebookPath,
  needsNoticeGate,
  notebookGateRedirect,
  noticeGateUrl,
  optionalDetailsAllowed,
  safeReturnPath,
} from './noticeGate'

function state(
  requiresAcknowledgement: boolean,
  optionalDetails: Partial<AccountPrivacyState['optionalDetails']> = {},
): AccountPrivacyState {
  return {
    currentNoticeVersion: 'v2',
    acknowledgement: requiresAcknowledgement
      ? null
      : { noticeVersion: 'v2', acknowledgedAt: '2026-09-25T10:00:00Z' },
    requiresAcknowledgement,
    optionalDetails: {
      currentStatementVersion: 's1',
      consent: null,
      transitionPending: false,
      pendingWorkoutCount: 0,
      ...optionalDetails,
    },
  }
}

const pending = { transitionPending: true, pendingWorkoutCount: 3 }
const consented = {
  consent: { statementVersion: 's1', consentedAt: '2026-09-28T10:00:00Z' },
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

// User story 6 (tasks.md T088): the notebook gate asks at most one thing per
// visit, the notice first, then the transition question — and only an account
// that still holds details without consent ever sees that question.
describe('notebookGateRedirect', () => {
  it('opens the notebook when nothing is pending', () => {
    expect(notebookGateRedirect(state(false), '/workouts')).toBeNull()
  })

  it('asks for the notice first, even when the transition is pending too', () => {
    expect(notebookGateRedirect(state(true, pending), '/workouts/12')).toBe(
      '/account/privacy/notice?returnTo=%2Fworkouts%2F12',
    )
  })

  it('asks the transition question once the notice is acknowledged', () => {
    expect(notebookGateRedirect(state(false, pending), '/workouts/12')).toBe(
      '/account/privacy/optional-details?returnTo=%2Fworkouts%2F12',
    )
  })

  // FR-031: refusing, or never having had details, never leads to a prompt.
  it('never asks an account without details', () => {
    expect(notebookGateRedirect(state(false), '/progress')).toBeNull()
  })

  it('never asks an account that has consent', () => {
    expect(
      notebookGateRedirect(state(false, { ...consented }), '/progress'),
    ).toBeNull()
  })

  it('never gates when the feature is off', () => {
    expect(notebookGateRedirect(null, '/workouts')).toBeNull()
  })
})

describe('optionalDetailsAllowed', () => {
  it('allows the details with consent', () => {
    expect(optionalDetailsAllowed(state(false, consented))).toBe(true)
  })

  it('hides them without consent', () => {
    expect(optionalDetailsAllowed(state(false))).toBe(false)
    expect(optionalDetailsAllowed(state(false, pending))).toBe(false)
  })

  // Flag off: production's behaviour until the switch, details as before.
  it('allows them when the feature is off', () => {
    expect(optionalDetailsAllowed(null)).toBe(true)
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
    '/account/privacy/optional-details',
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
