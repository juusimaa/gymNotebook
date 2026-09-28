import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import {
  getAccountPrivacy,
  getOptionalDetailsStatement,
  grantOptionalDetailsConsent,
  withdrawOptionalDetailsConsent,
  type AccountPrivacyState,
  type OptionalDetailsConsent as Consent,
  type OptionalDetailsStatement,
} from '../api/privacy'
import { safeReturnPath } from '../auth/noticeGate'
import { clearToken } from '../auth/token'
import './Privacy.css'

// Consent for a workout's title, location, notes and bodyweight (specs/001
// user story 6, contracts/ui.md → Optional-details consent transitions). Three
// parts, so the editor can reuse the first without leaving its draft:
//
//   - OptionalDetailsChoice: the versioned statement with two equally
//     prominent buttons, nothing preselected. Used inline in the workout
//     editor and on the screen below.
//   - WithdrawReview: what withdrawing removes, what stays, and an export
//     link, before anything is removed.
//   - The /account/privacy/optional-details screen, which also asks the
//     notebook gate's transition question.

function formatDate(instant: string): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'long' }).format(
    new Date(instant),
  )
}

function pluralWorkouts(count: number): string {
  return count === 1 ? '1 workout' : `${count} workouts`
}

// The statement's text, rendered as text nodes only, like the notice.
function StatementText({ statement }: { statement: OptionalDetailsStatement }) {
  return (
    <>
      <p className="notice-meta num">
        Statement version {statement.version} · effective{' '}
        {formatDate(statement.effectiveAt)}
      </p>
      <div className="notice-sections">
        {statement.sections.map((section) => (
          <section key={section.id} className="notice-section">
            <h3>{section.heading}</h3>
            {section.paragraphs.map((paragraph, i) => (
              <p key={i}>{paragraph}</p>
            ))}
          </section>
        ))}
      </div>
    </>
  )
}

// Loads the current statement and asks. "Allow" sends exactly the version on
// screen; a 409 means the statement changed while it was open, so the newer
// one replaces it and nothing is recorded. The decline button records nothing
// at all (FR-031) — it only tells the parent to move on.
export function OptionalDetailsChoice({
  declineLabel,
  onAllowed,
  onDeclined,
}: {
  declineLabel: string
  onAllowed: (consent: Consent) => void
  onDeclined: () => void
}) {
  const [statement, setStatement] = useState<
    OptionalDetailsStatement | null | undefined
  >(undefined)
  const [loadFailed, setLoadFailed] = useState(false)
  const [loadAttempt, setLoadAttempt] = useState(0)
  const [message, setMessage] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const loaded = await getOptionalDetailsStatement()
        if (!cancelled) setStatement(loaded)
      } catch {
        if (!cancelled) setLoadFailed(true)
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [loadAttempt])

  function retryLoad() {
    setLoadFailed(false)
    setStatement(undefined)
    setLoadAttempt((attempt) => attempt + 1)
  }

  async function allow(displayed: OptionalDetailsStatement) {
    setMessage(null)
    setSubmitting(true)
    try {
      onAllowed(await grantOptionalDetailsConsent(displayed.version))
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setMessage(
          'The statement changed while this was open. Please read the updated version before choosing.',
        )
        retryLoad()
      } else if (err instanceof ApiError && err.status === 503) {
        setMessage(
          'The notebook is busy for a moment. Nothing was recorded; please try again.',
        )
      } else {
        setMessage(
          "Couldn't record this. Please check your connection and try again.",
        )
      }
    } finally {
      setSubmitting(false)
    }
  }

  if (loadFailed) {
    return (
      <div className="consent-panel">
        <p className="form-message" role="alert">
          The statement could not be loaded. Please try again.
        </p>
        <div className="consent-choice">
          <button
            className="btn btn-secondary"
            type="button"
            onClick={retryLoad}
          >
            Try again
          </button>
          <button
            className="btn btn-secondary"
            type="button"
            onClick={onDeclined}
          >
            {declineLabel}
          </button>
        </div>
      </div>
    )
  }

  if (statement === undefined) {
    return (
      <div className="consent-panel" aria-busy="true">
        Opening the statement…
      </div>
    )
  }

  // Feature off: nothing to consent to, and the details aren't restricted.
  if (statement === null) {
    return (
      <div className="consent-panel">
        <p className="muted">Optional details need no permission here.</p>
      </div>
    )
  }

  return (
    <div className="consent-panel">
      <StatementText statement={statement} />
      {message !== null && (
        <p className="form-message" role="alert">
          {message}
        </p>
      )}
      {/* Equal prominence (FR-030): the same class and size, side by side. */}
      <div className="consent-choice">
        <button
          className="btn btn-secondary"
          type="button"
          disabled={submitting}
          onClick={() => void allow(statement)}
        >
          Allow
        </button>
        <button
          className="btn btn-secondary"
          type="button"
          disabled={submitting}
          onClick={onDeclined}
        >
          {declineLabel}
        </button>
      </div>
    </div>
  )
}

// The confirmation before withdrawal or "Don't allow" (FR-033). No password.
// A lost response is safe to retry: the server removes nothing twice and
// reports only what that request cleared.
export function WithdrawReview({
  pendingWorkoutCount,
  onDone,
  onCancel,
}: {
  // Known in the transition question; unknown (null) when withdrawing.
  pendingWorkoutCount: number | null
  onDone: (clearedWorkouts: number) => void
  onCancel: () => void
}) {
  const [message, setMessage] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function confirm() {
    setMessage(null)
    setSubmitting(true)
    try {
      const result = await withdrawOptionalDetailsConsent()
      onDone(result.clearedWorkouts)
    } catch (err) {
      setMessage(
        err instanceof ApiError && err.status === 503
          ? 'The notebook is busy for a moment. Nothing was removed; please try again.'
          : "Couldn't confirm this. Please check your connection and try again — trying again is safe.",
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="consent-panel">
      <p>
        {pendingWorkoutCount === null
          ? 'The title, location, notes and bodyweight will be permanently removed from every workout in your notebook.'
          : `The title, location, notes and bodyweight will be permanently removed from ${pluralWorkouts(pendingWorkoutCount)}.`}
      </p>
      <p>
        Your workouts, exercises and sets stay as they are. You can allow the
        details again later; the fields will start empty.
      </p>
      <p>
        <Link to="/account/export">Export your notebook first</Link> if you want
        a copy.
      </p>
      {message !== null && (
        <p className="form-message" role="alert">
          {message}
        </p>
      )}
      <div className="consent-choice">
        <button
          className="btn btn-primary"
          type="button"
          disabled={submitting}
          onClick={() => void confirm()}
        >
          {submitting ? 'Removing…' : 'Remove details'}
        </button>
        <button
          className="btn btn-secondary"
          type="button"
          disabled={submitting}
          onClick={onCancel}
        >
          Cancel
        </button>
      </div>
    </div>
  )
}

type Step =
  | { kind: 'ask' }
  | { kind: 'review' }
  | { kind: 'removed'; clearedWorkouts: number }

// /account/privacy/optional-details[?returnTo=…][&step=withdraw]. Under the
// auth guard but outside the notebook gate, like the other privacy screens.
//
//   - Reached from the notebook gate (returnTo set, transition pending): the
//     transition question. "Allow" keeps the details; "Don't allow" goes
//     through the same review as withdrawal. Either way, on to the notebook.
//     Leaving answers nothing, so the question comes back next visit.
//   - With consent: its date and a "Withdraw" action. ?step=withdraw starts at
//     the review, so withdrawing from the privacy screen takes no more steps
//     than granting.
//   - Without consent and nothing pending: the statement, "Allow" and "Not now".
export default function OptionalDetailsConsent() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const rawReturnTo = searchParams.get('returnTo')
  const fromGate = rawReturnTo !== null
  // A crafted returnTo can only ever lead to a notebook route (noticeGate.ts).
  const returnTo = safeReturnPath(rawReturnTo)
  const startWithWithdraw = searchParams.get('step') === 'withdraw'

  const [state, setState] = useState<AccountPrivacyState | null | undefined>(
    undefined,
  )
  const [loadMessage, setLoadMessage] = useState<string | null>(null)
  const [loadAttempt, setLoadAttempt] = useState(0)
  const [step, setStep] = useState<Step>({ kind: 'ask' })
  const [status, setStatus] = useState<string | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const loaded = await getAccountPrivacy()
        if (cancelled) return
        setState(loaded)
        if (startWithWithdraw && loaded?.optionalDetails.consent) {
          setStep({ kind: 'review' })
        }
      } catch (err) {
        if (cancelled) return
        if (err instanceof ApiError && err.status === 401) {
          clearToken()
          void navigate('/login')
          return
        }
        setLoadMessage(
          'Your privacy details could not be loaded. Please try again.',
        )
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [loadAttempt, navigate, startWithWithdraw])

  // Move focus to the heading whenever the step changes, so keyboard and
  // screen-reader users start at the top of what's now on screen.
  useEffect(() => {
    if (state) headingRef.current?.focus()
  }, [state, step])

  function retryLoad() {
    setLoadMessage(null)
    setState(undefined)
    setLoadAttempt((attempt) => attempt + 1)
  }

  function continueToNotebook() {
    // replace: Back from the notebook shouldn't land on a question answered.
    void navigate(returnTo, { replace: true })
  }

  function allowed(consent: Consent, current: AccountPrivacyState) {
    if (fromGate) {
      continueToNotebook()
      return
    }
    setState({
      ...current,
      optionalDetails: {
        ...current.optionalDetails,
        consent,
        transitionPending: false,
        pendingWorkoutCount: 0,
      },
    })
    setStatus(
      'Allowed. The title, location, notes and bodyweight fields now appear in the workout editor.',
    )
  }

  function removed(clearedWorkouts: number, current: AccountPrivacyState) {
    setState({
      ...current,
      optionalDetails: {
        ...current.optionalDetails,
        consent: null,
        transitionPending: false,
        pendingWorkoutCount: 0,
      },
    })
    setStep({ kind: 'removed', clearedWorkouts })
  }

  function signOut() {
    clearToken()
    void navigate('/login')
  }

  const backLink = (
    <p className="privacy-back">
      <Link to="/account/privacy" className="btn btn-ghost">
        ← Privacy &amp; account
      </Link>
    </p>
  )

  // From the gate, the same ways out as the notice gate: none of them answers
  // the question.
  const leaveLinks = (
    <p className="privacy-links">
      <Link to="/" className="btn btn-ghost">
        Back to the cover
      </Link>
      <Link to="/account/privacy" className="btn btn-ghost">
        Privacy &amp; account
      </Link>
      <button type="button" className="btn btn-ghost" onClick={signOut}>
        Sign out
      </button>
    </p>
  )

  if (loadMessage !== null) {
    return (
      <main className="page">
        <p className="form-message" role="alert">
          {loadMessage}
        </p>
        <button className="btn btn-secondary" type="button" onClick={retryLoad}>
          Try again
        </button>
        {fromGate ? leaveLinks : backLink}
      </main>
    )
  }

  if (state === undefined) {
    return (
      <main className="page" aria-busy="true">
        Opening optional details…
      </main>
    )
  }

  if (state === null) {
    return (
      <main className="page">
        <p className="muted">Privacy controls are not available.</p>
        {backLink}
      </main>
    )
  }

  const details = state.optionalDetails

  if (step.kind === 'removed') {
    return (
      <main className="page">
        {!fromGate && backLink}
        <p className="kicker">Optional workout details</p>
        <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
          Details removed
        </h2>
        <p role="status">
          {step.clearedWorkouts === 0
            ? 'No optional details were left to remove.'
            : `Removed the title, location, notes and bodyweight from ${pluralWorkouts(step.clearedWorkouts)}.`}{' '}
          The rest of your notebook is unchanged.
        </p>
        {fromGate && (
          <button
            className="btn btn-primary btn-block"
            type="button"
            onClick={continueToNotebook}
          >
            Continue to notebook
          </button>
        )}
      </main>
    )
  }

  if (step.kind === 'review') {
    return (
      <main className="page">
        {!fromGate && backLink}
        <p className="kicker">Optional workout details</p>
        <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
          Remove optional details?
        </h2>
        <WithdrawReview
          pendingWorkoutCount={
            details.transitionPending ? details.pendingWorkoutCount : null
          }
          onDone={(cleared) => removed(cleared, state)}
          onCancel={() => setStep({ kind: 'ask' })}
        />
        {fromGate && leaveLinks}
      </main>
    )
  }

  if (details.consent !== null) {
    return (
      <main className="page">
        {backLink}
        <p className="kicker">Optional workout details</p>
        <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
          Title, location, notes and bodyweight
        </h2>
        {status !== null && <p role="status">{status}</p>}
        <p>
          You allowed these details on {formatDate(details.consent.consentedAt)}{' '}
          (statement version {details.consent.statementVersion}).
        </p>
        <p className="muted">
          Withdrawing removes them from every workout. The rest of your notebook
          stays.
        </p>
        <button
          className="btn btn-secondary btn-block"
          type="button"
          onClick={() => setStep({ kind: 'review' })}
        >
          Withdraw
        </button>
      </main>
    )
  }

  if (details.transitionPending) {
    return (
      <main className="page">
        {!fromGate && backLink}
        <p className="kicker">Before you open the notebook</p>
        <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
          Keep your optional details?
        </h2>
        <p>
          {pluralWorkouts(details.pendingWorkoutCount)} in your notebook{' '}
          {details.pendingWorkoutCount === 1 ? 'has' : 'have'} a title,
          location, notes or bodyweight. The service now keeps these only with
          your permission. Read what that means below, then choose.
        </p>
        <p>
          <Link to="/account/export">Export your notebook</Link> first if you
          want a copy.
        </p>
        <OptionalDetailsChoice
          declineLabel="Don't allow"
          onAllowed={(consent) => allowed(consent, state)}
          onDeclined={() => setStep({ kind: 'review' })}
        />
        {fromGate && leaveLinks}
      </main>
    )
  }

  return (
    <main className="page">
      {backLink}
      <p className="kicker">Optional workout details</p>
      <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Title, location, notes and bodyweight
      </h2>
      <OptionalDetailsChoice
        declineLabel="Not now"
        onAllowed={(consent) => allowed(consent, state)}
        onDeclined={() =>
          void navigate(fromGate ? returnTo : '/account/privacy')
        }
      />
    </main>
  )
}
