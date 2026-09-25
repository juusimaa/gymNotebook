import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { ApiError } from '../api/client'
import { deleteAccount } from '../api/privacy'
import { endSessionHere } from '../auth/invalidation'
import {
  describeDeletionFailure,
  type DeletionFailure,
} from './deletionOutcome'
import './Privacy.css'

// /account/delete — "Delete your account" (specs/001 user story 4,
// contracts/ui.md → Deletion transitions). Under the auth guard but outside
// the notice gate, like the rest of Privacy & account. Two steps, so the
// irreversible action is never one tap from the explanation:
//
//   review  — what goes, what stays for a while and until when, the optional
//             export, and "Keep my account".
//   confirm — the current password and the deletion button itself.
//
// Submitting disables the button until the answer arrives. There is no Cancel
// once it's sent: the server finishes a deletion it has started even if the
// page closes, and the copy says so. Success signs this browser out and moves
// to /account/deleted with the server's dates. Failures either keep the form
// (nothing was deleted, retry is safe) or replace it with an explanation that
// doesn't claim either outcome (deletionOutcome.ts).

type Step = 'review' | 'confirm'

export default function DeleteAccount() {
  const navigate = useNavigate()
  const [step, setStep] = useState<Step>('review')
  const [password, setPassword] = useState('')
  const [deleting, setDeleting] = useState(false)
  const [failure, setFailure] = useState<DeletionFailure | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const passwordRef = useRef<HTMLInputElement>(null)
  const unknownRef = useRef<HTMLHeadingElement>(null)

  // Focus follows the step: the confirm heading when it opens, the password
  // field after a retryable failure, the explanation after an unknown outcome.
  useEffect(() => {
    if (step === 'confirm') headingRef.current?.focus()
  }, [step])
  useEffect(() => {
    if (failure?.kind === 'retry') passwordRef.current?.focus()
    if (failure?.kind === 'unknown') unknownRef.current?.focus()
  }, [failure])

  async function submit() {
    const typed = password
    setPassword('')
    setFailure(null)
    setDeleting(true)
    try {
      const outcome = await deleteAccount(typed)
      // The account is gone: clear everything this browser holds for it. Other
      // tabs follow through the token's storage event.
      endSessionHere()
      await navigate('/account/deleted', { replace: true, state: outcome })
    } catch (err) {
      // A 401 means this session is over whatever else happened, so it ends
      // here too — but the screen explains, rather than dropping to sign-in.
      if (err instanceof ApiError && err.status === 401) {
        endSessionHere()
      }
      setFailure(describeDeletionFailure(err))
      setDeleting(false)
    }
  }

  const back = (
    <p className="privacy-back">
      <Link to="/account/privacy" className="btn btn-ghost">
        ← Privacy &amp; account
      </Link>
    </p>
  )

  if (failure?.kind === 'unknown') {
    return (
      <main className="page">
        <p className="kicker">Delete your account</p>
        <h2 className="privacy-heading" ref={unknownRef} tabIndex={-1}>
          {failure.heading}
        </h2>
        <p className="export-intro" role="alert">
          {failure.message}
        </p>
        <p className="privacy-links">
          {failure.signedOut ? (
            <Link to="/login" className="btn btn-primary">
              Sign in again
            </Link>
          ) : (
            <Link to="/" className="btn btn-primary">
              Check my account
            </Link>
          )}
          <Link to="/privacy#notice-contact" className="btn btn-ghost">
            Privacy contact
          </Link>
        </p>
      </main>
    )
  }

  if (step === 'review') {
    return (
      <main className="page">
        {back}
        <p className="kicker">Before you leave</p>
        <h2 className="privacy-heading">Delete your account</h2>
        <p className="export-intro">
          This permanently removes your account, exercises, sessions, notes and
          sets, and the record of which privacy notice you continued past. Every
          signed-in session loses access. You cannot undo this.
        </p>
        <Link to="/account/export" className="btn btn-ghost btn-block">
          Export first (optional)
        </Link>

        <section className="privacy-block" aria-labelledby="deletion-remains">
          <h3 id="deletion-remains">What remains for a limited time</h3>
          <p>
            Backup copies may keep your notebook for up to 30 calendar days
            after the deletion. They can&apos;t be used as your notebook.
          </p>
          <p>
            A minimal deletion record stays in the service logs for up to 31
            days: a random account identifier and the time of the deletion,
            nothing else. It exists so that restoring a backup can&apos;t bring
            your account back.
          </p>
          <p>
            Necessary, restricted security logs collected before the deletion
            are not erased early. Each expires 30 days after it was collected.
          </p>
        </section>

        <div className="form-stack export-form">
          <button
            className="btn btn-secondary btn-block"
            type="button"
            onClick={() => setStep('confirm')}
          >
            Continue to delete
          </button>
          <Link to="/account/privacy" className="btn btn-ghost btn-block">
            Keep my account
          </Link>
        </div>
      </main>
    )
  }

  return (
    <main className="page">
      {back}
      <p className="kicker">Delete your account</p>
      <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Confirm with your password
      </h2>
      <p className="export-intro">
        Deleting your account can&apos;t be undone. Your account and notebook
        are removed as soon as you confirm.
      </p>

      <form
        className="form-stack export-form"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        <label className="field">
          <span className="label">Current password</span>
          <input
            ref={passwordRef}
            className="input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            disabled={deleting}
            required
          />
        </label>

        {failure !== null && (
          <p className="form-message" role="alert">
            {failure.message}
          </p>
        )}

        <div role="status" aria-live="polite">
          {deleting && (
            <p className="export-progress">
              Deleting your account… Closing this page won&apos;t stop it.
            </p>
          )}
        </div>

        <button
          className="btn btn-secondary btn-block"
          type="submit"
          disabled={deleting}
        >
          Delete my account permanently
        </button>
        {!deleting && (
          <Link to="/account/privacy" className="btn btn-ghost btn-block">
            Keep my account
          </Link>
        )}
      </form>
    </main>
  )
}
