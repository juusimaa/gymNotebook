import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { confirmPasswordReset } from '../api/auth'
import { describeAuthError } from '../api/authErrors'
import { ApiError } from '../api/client'
import type { LoginEntryState } from '../auth/loginEntry'
import {
  linkFailure,
  readFragmentToken,
  removeFragmentFromUrl,
  type LinkFailure,
} from '../auth/linkToken'
import { setToken } from '../auth/token'
import './Login.css'

// /reset-password#token=… (specs/002 contracts/ui.md). Public: the reset email
// is opened wherever the mail app opens links, often a browser with no session.
// Reached from the password-reset email and from the finish-signup email, which
// carries the same kind of link.
//
// The token is read once from the fragment and removed from the address bar
// straight away. Unlike /verify-email, nothing is posted on load: the link
// only shows a form, so a mail scanner that opens it changes nothing (spec
// Edge Cases). The token is checked when the form is submitted.

// describeAuthError's 400 and 403 copy is about signing in; these are the ones
// this form needs. A link failure never gets here — it has its own state.
function describeError(err: unknown): string {
  if (err instanceof ApiError && err.status === 400) {
    return "That password can't be used. It can't be blank, or longer than 72 characters."
  }
  return describeAuthError(err)
}

export default function ResetPassword() {
  const navigate = useNavigate()

  // Read during the first render, before the effect below strips it from the
  // URL. No token at all is the same as a broken link.
  const [token] = useState(() => readFragmentToken(window.location.hash))
  const [failure, setFailure] = useState<LinkFailure | null>(
    token === null ? 'invalid' : null,
  )
  const [newPassword, setNewPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    removeFragmentFromUrl()
  }, [])

  const failureHeadingRef = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    if (failure !== null) failureHeadingRef.current?.focus()
  }, [failure])

  async function submit(linkToken: string) {
    setMessage(null)
    // Caught here, before a request: the server only sees one password, so it
    // could never tell that the two differed.
    if (newPassword !== repeat) {
      setMessage("The two passwords don't match")
      return
    }
    setSubmitting(true)
    try {
      const { token: sessionToken } = await confirmPasswordReset({
        token: linkToken,
        newPassword,
      })
      // The reset signed this browser in. Store the token before navigating,
      // or the guard's /auth/me would find no session; `replace` keeps this
      // spent link page out of the back history.
      setToken(sessionToken)
      void navigate('/', { replace: true })
    } catch (err) {
      const linkError = linkFailure(err)
      // 400 expired/invalid is the link; 400 invalid_request is the password.
      if (
        linkError !== null &&
        !(err instanceof ApiError && err.code === 'invalid_request')
      ) {
        setFailure(linkError)
        return
      }
      setMessage(describeError(err))
    } finally {
      setSubmitting(false)
    }
  }

  // "Send a new link" opens /login in Forgot mode, saying why
  // (auth/loginEntry.ts).
  function requestNewLink(reason: LinkFailure) {
    const state: LoginEntryState = { mode: 'forgot', reason }
    void navigate('/login', { state })
  }

  return (
    <main className="page">
      <p className="kicker">Training log</p>
      <h1>
        Gym
        <br />
        Notebook
      </h1>
      <div className="accent-rule" />

      {failure !== null || token === null ? (
        <section className="login-inbox">
          <h2 ref={failureHeadingRef} tabIndex={-1}>
            This link no longer works
          </h2>
          <p>
            {failure === 'expired'
              ? 'Reset links work for one hour.'
              : 'Each reset link works once.'}{' '}
            You can have a new one sent.
          </p>
          <button
            type="button"
            className="btn btn-primary btn-block"
            onClick={() => requestNewLink(failure ?? 'invalid')}
          >
            Send a new link
          </button>
        </section>
      ) : (
        <>
          <p className="muted login-intro">
            Choose a new password for your notebook.
          </p>
          <form
            className="form-stack"
            onSubmit={(e) => {
              e.preventDefault()
              void submit(token)
            }}
          >
            <label className="field">
              <span className="label">New password</span>
              <input
                className="input"
                type="password"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                autoComplete="new-password"
                required
              />
            </label>
            <label className="field">
              <span className="label">Repeat new password</span>
              <input
                className="input"
                type="password"
                value={repeat}
                onChange={(e) => setRepeat(e.target.value)}
                autoComplete="new-password"
                required
              />
            </label>

            <button
              className="btn btn-primary btn-block login-submit"
              type="submit"
              disabled={submitting}
            >
              Set password
            </button>

            {/* Said before, not after: once it's done, this browser goes
                straight into the notebook (spec Edge Cases). */}
            <p className="muted login-service">
              This signs you in here and out everywhere else, including the app
              on your home screen.
            </p>
            {message !== null && (
              <p className="form-message" role="alert">
                {message}
              </p>
            )}
          </form>
        </>
      )}
    </main>
  )
}
