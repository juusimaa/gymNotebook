import { ApiError, request } from './client'

// Wire types and calls for the privacy notice routes (specs/001 user story 1,
// contracts/api.md), one per record in backend/GymNotebook.Api.
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
