import { ApiError, request, send } from './client'
import { readCompleteJson } from './download'

// Wire types and calls for the privacy routes (specs/001 user stories 1, 3 and
// 4, contracts/api.md), one per record in backend/GymNotebook.Api.
//
// The frontend has no feature flag of its own (plan.md P25): the backend maps
// these routes only when PRIVACY_LIFECYCLE_ENABLED is "true", so a 404 here
// means "feature off" and the calls return null for it. Every other failure
// still throws, so a screen can tell "off" from "broken".

export interface NoticeSection {
  id: string
  heading: string
  // Plain text only. Rendered as text nodes, never as HTML, so nothing in a
  // notice file can put markup on the page.
  paragraphs: string[]
}

export interface AnnouncedNotice {
  version: string
  effectiveAt: string
  materialChangeSummary: string
  sections: NoticeSection[]
}

export interface PrivacyNotice {
  version: string
  // UTC instants as ISO-8601 strings, as System.Text.Json writes DateTimeOffset.
  effectiveAt: string
  publishedAt: string
  materialChangeSummary: string
  sections: NoticeSection[]
  // A future version, shown before it takes effect (FR-003); null when none.
  announcedSuccessor: AnnouncedNotice | null
}

export interface NoticeAcknowledgement {
  noticeVersion: string
  acknowledgedAt: string
}

export interface AccountPrivacyState {
  currentNoticeVersion: string
  acknowledgement: NoticeAcknowledgement | null
  // Decided by the server; the client never compares version strings itself.
  requiresAcknowledgement: boolean
}

// Turns the 404 of an unmapped route into null; anything else propagates.
async function nullWhenFeatureOff<T>(pending: Promise<T>): Promise<T | null> {
  try {
    return await pending
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      return null
    }
    throw err
  }
}

// Public: works signed out. Fetching it records nothing.
export function getPrivacyNotice(): Promise<PrivacyNotice | null> {
  return nullWhenFeatureOff(request<PrivacyNotice>('/privacy/notice'))
}

export function getAccountPrivacy(): Promise<AccountPrivacyState | null> {
  return nullWhenFeatureOff(request<AccountPrivacyState>('/account/privacy'))
}

// Sends exactly the version the gate displayed. Throws ApiError 409 with code
// "notice_version_changed" when a newer version took effect in the meantime.
// Not wrapped in nullWhenFeatureOff: the gate only calls this after it has
// loaded a notice, so a 404 here is an error, not "off".
export function acknowledgeNotice(
  noticeVersion: string,
): Promise<NoticeAcknowledgement> {
  return request<NoticeAcknowledgement>('/account/privacy/acknowledgement', {
    method: 'PUT',
    body: { noticeVersion },
  })
}

// The export's fixed file name; the server sends the same one in
// Content-Disposition, which a cross-origin fetch can't read without extra CORS
// configuration, so the client names the file itself.
export const EXPORT_FILE_NAME = 'gym-notebook-export.json'

// POST /account/export (user story 3): the whole notebook as one JSON file.
// Resolves only with a complete file (see readCompleteJson). Rejects with
// ApiError 400 "password_verification_failed" for a wrong password, 401 when the
// session no longer works, 429 for too many attempts or "export_in_progress",
// 503 "temporarily_unavailable"; with fetch's own error when the connection
// broke; and with an AbortError when `signal` aborts. `onReceiving` fires once
// the password was accepted and the file has started arriving. The password is
// sent as typed and kept nowhere, so a retry always asks for it again. The body
// is read inside send(), so a sign-out anywhere aborts the download too.
export function exportNotebook(
  currentPassword: string,
  options: { signal?: AbortSignal; onReceiving?: () => void } = {},
): Promise<Blob> {
  return send(
    '/account/export',
    {
      method: 'POST',
      body: { currentPassword },
      signal: options.signal,
      // A POST that only reads: a 401 here can't have left a half-saved change.
      changesData: false,
    },
    (response) => {
      options.onReceiving?.()
      return readCompleteJson(response)
    },
  )
}

// The success body of POST /account/delete (contracts/api.md → Deletion
// response): only dates and one sentence, nothing about the account.
export interface DeletionOutcome {
  status: 'deleted'
  // UTC instants as ISO-8601 strings. The boundary is when retention started
  // counting, just before the deletion committed.
  retentionBoundaryAt: string
  backupsExpireBy: string
  deletionEvidenceExpiresBy: string
  logRetentionNotice: string
}

// POST /account/delete (user story 4). Resolves only when the deletion has
// committed. Rejects with ApiError 400 "password_verification_failed" for a
// wrong password, 429 for too many attempts, 503 "temporarily_unavailable" when
// nothing was deleted and a retry is safe, 503 "deletion_outcome_unknown" when
// the server couldn't tell, 401 when this session no longer works — which never
// proves the deletion happened, so the screen handles it itself
// (unauthorized: 'local') — and with fetch's own error when the connection
// broke, whose outcome is unknown too. Never retried automatically: the
// password is sent once and kept nowhere.
export function deleteAccount(
  currentPassword: string,
): Promise<DeletionOutcome> {
  return request<DeletionOutcome>('/account/delete', {
    method: 'POST',
    body: { currentPassword, confirmDeletion: true },
    unauthorized: 'local',
  })
}

// Checks that a value (the completion screen's router state, which is only
// `unknown` to TypeScript) really is a deletion outcome, so that screen can
// never claim a deletion from anything else.
export function isDeletionOutcome(value: unknown): value is DeletionOutcome {
  if (typeof value !== 'object' || value === null) {
    return false
  }
  const record = value as Record<string, unknown>
  return (
    record.status === 'deleted' &&
    typeof record.retentionBoundaryAt === 'string' &&
    typeof record.backupsExpireBy === 'string' &&
    typeof record.deletionEvidenceExpiresBy === 'string' &&
    typeof record.logRetentionNotice === 'string'
  )
}
