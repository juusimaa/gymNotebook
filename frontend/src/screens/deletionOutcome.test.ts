import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { isDeletionOutcome } from '../api/privacy'
import { describeDeletionFailure } from './deletionOutcome'

// Which deletion failures are known to have changed nothing (retry), and which
// can't be told apart from success (unknown). Getting this wrong either way is
// the harm: a false "nothing was deleted", or a false "deleted".

describe('describeDeletionFailure', () => {
  it.each([
    [new ApiError(400, 'password_verification_failed')],
    [new ApiError(400, 'invalid_request')],
    [new ApiError(429)],
    [new ApiError(503, 'temporarily_unavailable')],
    [new ApiError(404)],
  ])('keeps the form for a failure that deleted nothing (%o)', (err) => {
    expect(describeDeletionFailure(err).kind).toBe('retry')
  })

  it('never reads a 401 as success or rollback, and says the session ended', () => {
    const failure = describeDeletionFailure(new ApiError(401))

    expect(failure).toMatchObject({ kind: 'unknown', signedOut: true })
  })

  it.each([
    ['an unconfirmed commit', new ApiError(503, 'deletion_outcome_unknown')],
    ['a broken connection', new TypeError('Failed to fetch')],
    ['an unexpected status', new ApiError(500)],
    ['a 503 without a known code', new ApiError(503)],
  ])('treats %s as an unknown outcome', (_label, err) => {
    expect(describeDeletionFailure(err)).toMatchObject({
      kind: 'unknown',
      signedOut: false,
    })
  })
})

describe('isDeletionOutcome', () => {
  const outcome = {
    status: 'deleted',
    retentionBoundaryAt: '2026-09-25T12:00:00Z',
    backupsExpireBy: '2026-10-25T12:00:00Z',
    deletionEvidenceExpiresBy: '2026-10-26T12:00:00Z',
    logRetentionNotice: 'Restricted logs expire within 30 days of collection.',
  }

  it('accepts the server response', () => {
    expect(isDeletionOutcome(outcome)).toBe(true)
  })

  // The completion screen reads router state, which could be anything.
  it.each([
    [null],
    [undefined],
    ['deleted'],
    [{ ...outcome, status: 'pending' }],
    [{ ...outcome, backupsExpireBy: undefined }],
  ])('rejects anything else (%o)', (value) => {
    expect(isDeletionOutcome(value)).toBe(false)
  })
})
