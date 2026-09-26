import { useEffect, useRef } from 'react'
import { Link, useLocation } from 'react-router'
import { isDeletionOutcome } from '../api/privacy'
import './Privacy.css'

// /account/deleted — the completion screen after a deletion (specs/001
// contracts/ui.md). Public: the account no longer exists, so nothing here can
// sit behind the auth guard. It renders a deletion only from the router state
// the deletion screen passes after the server's success response — never from
// the URL — so typing the address, or arriving from anywhere else, shows a
// neutral signed-out screen that claims nothing.

function formatDate(instant: string): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'long' }).format(
    new Date(instant),
  )
}

export default function AccountDeleted() {
  // Router state is typed `any`; `unknown` makes the check below mandatory.
  const state: unknown = useLocation().state
  const outcome = isDeletionOutcome(state) ? state : null
  const headingRef = useRef<HTMLHeadingElement>(null)

  // Land keyboard and screen-reader users on the outcome, not wherever focus
  // was on the previous screen.
  useEffect(() => {
    headingRef.current?.focus()
  }, [])

  if (outcome === null) {
    return (
      <main className="page">
        <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
          You&apos;re signed out
        </h2>
        <p className="privacy-links">
          <Link to="/login" className="btn btn-primary">
            Sign in
          </Link>
        </p>
      </main>
    )
  }

  return (
    <main className="page">
      <p className="kicker">Account closed</p>
      <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Your account is deleted
      </h2>
      <p className="export-intro" role="status">
        Your account and notebook have been removed, and every session is signed
        out.
      </p>

      <section className="privacy-block" aria-labelledby="deleted-remains">
        <h3 id="deleted-remains">What remains, and until when</h3>
        <p>
          Backup copies expire by {formatDate(outcome.backupsExpireBy)}. They
          can&apos;t be used as your notebook.
        </p>
        <p>
          The minimal deletion record (a random account identifier and the time
          of deletion) expires by{' '}
          {formatDate(outcome.deletionEvidenceExpiresBy)}.
        </p>
        <p>{outcome.logRetentionNotice}</p>
        <p className="muted">
          Files you downloaded, such as an export, stay on your device.
        </p>
      </section>

      <p className="privacy-links">
        <Link to="/login" className="btn btn-primary">
          Return to sign in
        </Link>
      </p>
    </main>
  )
}
