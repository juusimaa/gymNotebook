import { useEffect, useRef, useState } from 'react'
import { Link, useLocation } from 'react-router'
import { getPrivacyNotice, type PrivacyNotice } from '../api/privacy'
import { getToken } from '../auth/token'
import NoticeContent from './NoticeContent'
import './Privacy.css'

// /privacy — the public privacy notice (specs/001 contracts/ui.md). Outside the
// auth guard: readable before registration (FR-001), linked from the login
// screen and from account privacy. Reading it records nothing; only the gate's
// Continue does.
export default function PrivacyNoticeScreen() {
  const location = useLocation()
  // undefined = loading, null = feature off (404), otherwise the notice.
  const [notice, setNotice] = useState<PrivacyNotice | null | undefined>(
    undefined,
  )
  const [message, setMessage] = useState<string | null>(null)
  const [loadAttempt, setLoadAttempt] = useState(0)
  const headingRef = useRef<HTMLHeadingElement>(null)

  // The same cancellation-flag pattern as Sessions.tsx: a response arriving
  // after unmount (or Strict Mode's second mount) is ignored.
  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const loaded = await getPrivacyNotice()
        if (!cancelled) setNotice(loaded)
      } catch {
        if (!cancelled) {
          setMessage(
            'The privacy notice could not be loaded. Please try again.',
          )
        }
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [loadAttempt])

  // Once the notice is on screen: jump to a linked section (/privacy#notice-
  // contact from account privacy) — the router doesn't scroll to hashes, and
  // the target didn't exist before the fetch — or else move focus to the
  // heading so a screen reader starts reading at the notice.
  useEffect(() => {
    if (!notice) return
    const target =
      location.hash === ''
        ? null
        : document.getElementById(location.hash.slice(1))
    if (target !== null) {
      target.scrollIntoView()
    } else {
      headingRef.current?.focus()
    }
  }, [notice, location.hash])

  function retry() {
    setMessage(null)
    setNotice(undefined)
    setLoadAttempt((attempt) => attempt + 1)
  }

  // Signed-in readers came from account privacy; everyone else from sign-in.
  const back =
    getToken() === null ? (
      <Link to="/login" className="btn btn-ghost">
        ← Back to sign in
      </Link>
    ) : (
      <Link to="/account/privacy" className="btn btn-ghost">
        ← Back to privacy &amp; account
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
        <p className="privacy-links">{back}</p>
      </main>
    )
  }

  if (notice === undefined) {
    return (
      <main className="page" aria-busy="true">
        Opening the notice…
      </main>
    )
  }

  if (notice === null) {
    return (
      <main className="page">
        <p className="muted">The privacy notice is not available.</p>
        <p className="privacy-links">{back}</p>
      </main>
    )
  }

  return (
    <main className="page">
      <p className="privacy-back">{back}</p>
      <p className="kicker">Privacy</p>
      <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Privacy notice
      </h2>
      <NoticeContent notice={notice} />
    </main>
  )
}
