import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import {
  acknowledgeNotice,
  getPrivacyNotice,
  type PrivacyNotice,
} from '../api/privacy'
import { safeReturnPath } from '../auth/noticeGate'
import { clearToken } from '../auth/token'
import NoticeContent from './NoticeContent'
import './Privacy.css'

// /account/privacy/notice?returnTo=… — the notebook gate (specs/001
// contracts/ui.md → Notice transitions). requireNoticeAcknowledged sends an
// account here, before any notebook fetch, when it hasn't continued past the
// current notice version.
//
//   - Continue sends exactly the version on screen; only a success opens the
//     notebook, at the validated return path.
//   - 409: a newer version took effect while this was open. Reload it and say
//     so; nothing was recorded.
//   - Network or server failure: stay here and offer a retry.
//   - Leaving (back to the cover, privacy, sign out) records nothing, so the
//     notice appears again on the next attempt to open the notebook.
//
// No checkbox and no consent wording: Continue acknowledges that the notice
// was shown, which is all that is stored.
export default function NoticeGate() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  // Validated once, here: a crafted returnTo can only ever lead to a notebook
  // route on this site (noticeGate.ts).
  const returnTo = safeReturnPath(searchParams.get('returnTo'))

  const [notice, setNotice] = useState<PrivacyNotice | null | undefined>(
    undefined,
  )
  const [loadMessage, setLoadMessage] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [loadAttempt, setLoadAttempt] = useState(0)
  const [submitting, setSubmitting] = useState(false)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const loaded = await getPrivacyNotice()
        if (cancelled) return
        if (loaded === null) {
          // Feature off: there is no gate, so go straight on.
          void navigate(returnTo, { replace: true })
          return
        }
        setNotice(loaded)
      } catch {
        if (!cancelled) {
          setLoadMessage(
            'The privacy notice could not be loaded. Please try again.',
          )
        }
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [loadAttempt, navigate, returnTo])

  // Start keyboard and screen-reader users at the notice's heading, both on
  // first load and after a 409 swapped in the newer version.
  useEffect(() => {
    if (notice) headingRef.current?.focus()
  }, [notice])

  function retryLoad() {
    setLoadMessage(null)
    setNotice(undefined)
    setLoadAttempt((attempt) => attempt + 1)
  }

  async function continueToNotebook(displayed: PrivacyNotice) {
    setMessage(null)
    setSubmitting(true)
    try {
      await acknowledgeNotice(displayed.version)
      // replace: Back from the notebook shouldn't land on a gate already passed.
      void navigate(returnTo, { replace: true })
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setMessage(
          'The notice changed while this page was open. Please read the updated version, then continue.',
        )
        try {
          const newer = await getPrivacyNotice()
          if (newer !== null) setNotice(newer)
        } catch {
          // The old text must not stay on screen as if it were current, so
          // fall back to the load-failure state and its retry.
          setNotice(undefined)
          setLoadMessage(
            'The notice has changed, but the new version could not be loaded. Please try again.',
          )
        }
      } else if (err instanceof ApiError && err.status === 401) {
        clearToken()
        void navigate('/login')
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

  function signOut() {
    clearToken()
    void navigate('/login')
  }

  // Ways out that record nothing, shown in every state.
  const leave = (
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
        {leave}
      </main>
    )
  }

  if (!notice) {
    return (
      <main className="page" aria-busy="true">
        Opening the notice…
      </main>
    )
  }

  return (
    <main className="page">
      <p className="kicker">Before you open the notebook</p>
      <h2 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Privacy notice
      </h2>
      <NoticeContent notice={notice} />

      <div className="notice-gate-footer">
        <p className="muted">
          Continue records the version shown to you. It does not record consent.
        </p>
        <button
          className="btn btn-primary btn-block"
          type="button"
          disabled={submitting}
          onClick={() => void continueToNotebook(notice)}
        >
          Continue to notebook
        </button>
        {message !== null && (
          <p className="form-message" role="alert">
            {message}
          </p>
        )}
      </div>
      {leave}
    </main>
  )
}
