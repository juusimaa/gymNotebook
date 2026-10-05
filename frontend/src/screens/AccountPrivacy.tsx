import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { ApiError } from '../api/client'
import { getAccountPrivacy, type AccountPrivacyState } from '../api/privacy'
import { clearToken } from '../auth/token'
import { formatInstantDate } from './workoutFormat'
import './Privacy.css'

// /account/privacy — "Privacy & account", reached from the cover (specs/001
// contracts/ui.md). Under the auth guard but outside the notice gate, so it
// stays usable before the current notice is acknowledged; opening it records
// nothing. It links to the notice, the export and account deletion.

// The acknowledgement status in words. "Continued past" rather than "accepted"
// or "agreed to": the record is that the notice was shown, not consent.
function describeStatus(state: AccountPrivacyState): string {
  const ack = state.acknowledgement
  if (ack === null) {
    return `You haven't continued past the current notice (version ${state.currentNoticeVersion}) yet. It will be shown when you next open the notebook.`
  }
  if (state.requiresAcknowledgement) {
    return `A new notice (version ${state.currentNoticeVersion}) is in effect. You last continued past version ${ack.noticeVersion} on ${formatInstantDate(ack.acknowledgedAt)}. The new version will be shown when you next open the notebook.`
  }
  return `You continued past the current notice (version ${ack.noticeVersion}) on ${formatInstantDate(ack.acknowledgedAt)}.`
}

// User story 6's consent status in words, for the optional-details block.
function describeOptionalDetails(state: AccountPrivacyState): string {
  const details = state.optionalDetails
  if (details.consent !== null) {
    return `You allowed a title, location, notes and bodyweight on your workouts on ${formatInstantDate(details.consent.consentedAt)}.`
  }
  if (details.transitionPending) {
    const workouts =
      details.pendingWorkoutCount === 1
        ? '1 workout has'
        : `${details.pendingWorkoutCount} workouts have`
    return `${workouts} a title, location, notes or bodyweight from before this choice existed. You'll be asked whether to keep them when you next open the notebook.`
  }
  return 'Not allowed. The workout editor hides the title, location, notes and bodyweight fields; everything else works without them.'
}

export default function AccountPrivacy() {
  const navigate = useNavigate()
  // undefined = loading, null = feature off (404), otherwise the state.
  const [state, setState] = useState<AccountPrivacyState | null | undefined>(
    undefined,
  )
  const [message, setMessage] = useState<string | null>(null)
  const [loadAttempt, setLoadAttempt] = useState(0)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const loaded = await getAccountPrivacy()
        if (!cancelled) setState(loaded)
      } catch (err) {
        if (cancelled) return
        // The token was revoked since the guard checked it (password changed
        // or account deleted elsewhere): same handling as the guard's.
        if (err instanceof ApiError && err.status === 401) {
          clearToken()
          void navigate('/login')
          return
        }
        setMessage(
          'Your privacy details could not be loaded. Please try again.',
        )
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [loadAttempt, navigate])

  // Focus the heading once loaded, so keyboard and screen-reader users land at
  // the top of the new screen rather than wherever focus was on the cover.
  useEffect(() => {
    if (state) headingRef.current?.focus()
  }, [state])

  function retry() {
    setMessage(null)
    setState(undefined)
    setLoadAttempt((attempt) => attempt + 1)
  }

  const backToCover = (
    <Link to="/" className="btn btn-ghost">
      ← Back to the cover
    </Link>
  )

  if (message !== null) {
    return (
      <main className="page">
        <p className="form-message" role="alert">
          {message}
        </p>
        <button className="btn btn-secondary" type="button" onClick={retry}>
          Try again
        </button>
        <p className="privacy-links">{backToCover}</p>
      </main>
    )
  }

  if (state === undefined) {
    return (
      <main className="page" aria-busy="true">
        Opening your privacy details…
      </main>
    )
  }

  if (state === null) {
    return (
      <main className="page">
        <p className="muted">Privacy controls are not available.</p>
        <p className="privacy-links">{backToCover}</p>
      </main>
    )
  }

  return (
    <main className="page">
      <p className="privacy-back">{backToCover}</p>
      <p className="kicker">Account</p>
      <h1 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Privacy &amp; account
      </h1>

      <section className="privacy-block" aria-labelledby="privacy-notice">
        <h2 id="privacy-notice">Privacy notice</h2>
        <p>{describeStatus(state)}</p>
        <p className="muted">
          Continuing past the notice records which version was shown to you. It
          does not record consent.
        </p>
        <Link to="/privacy" className="btn btn-secondary btn-block">
          Read the privacy notice
        </Link>
      </section>

      <section className="privacy-block" aria-labelledby="privacy-details">
        <h2 id="privacy-details">Optional workout details</h2>
        <p>{describeOptionalDetails(state)}</p>
        {state.optionalDetails.consent !== null ? (
          // Straight to the review step: withdrawing takes no more steps than
          // allowing did (FR-033).
          <Link
            to="/account/privacy/optional-details?step=withdraw"
            className="btn btn-secondary btn-block"
          >
            Withdraw
          </Link>
        ) : (
          <Link
            to="/account/privacy/optional-details"
            className="btn btn-secondary btn-block"
          >
            {state.optionalDetails.transitionPending
              ? 'Choose now'
              : 'Review and allow'}
          </Link>
        )}
      </section>

      <section className="privacy-block" aria-labelledby="privacy-export">
        <h2 id="privacy-export">Export your notebook</h2>
        <p>
          Get one JSON file with your account, exercises, sessions and sets.
        </p>
        <Link to="/account/export" className="btn btn-secondary btn-block">
          Export my data
        </Link>
      </section>

      <section className="privacy-block" aria-labelledby="privacy-delete">
        <h2 id="privacy-delete">Delete your account</h2>
        <p>
          Permanently remove your account and notebook, and sign out every
          session. The next screen explains what goes and what remains for a
          limited time before anything is deleted.
        </p>
        <Link to="/account/delete" className="btn btn-secondary btn-block">
          Delete my account
        </Link>
      </section>

      <section className="privacy-block" aria-labelledby="privacy-contact">
        <h2 id="privacy-contact">Questions and requests</h2>
        <p>
          For questions about your information, or to ask for access, correction
          or deletion, use the contact details in the notice.
        </p>
        <Link to="/privacy#notice-contact" className="btn btn-ghost">
          Privacy contact
        </Link>
      </section>
    </main>
  )
}
