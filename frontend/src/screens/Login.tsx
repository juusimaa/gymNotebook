import { useEffect, useRef, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { login, register, resendVerification } from '../api/auth'
import {
  describeAuthError,
  INTERRUPTED_WRITE_NOTICE,
  isEmailNotVerified,
} from '../api/authErrors'
import { getPrivacyNotice } from '../api/privacy'
import {
  clearInterruptedWrite,
  hasInterruptedWrite,
} from '../auth/invalidation'
import { setToken } from '../auth/token'
import './Login.css'

// docs/ui/README.md, screen 1, and specs/002 contracts/ui.md → /login. One
// route, three modes switched in place rather than separate pages, so the
// browser's back button leaves the screen instead of stepping through them:
//
//   signIn  Email + password → the notebook. A 403 email_not_verified (right
//           password, unconfirmed address) switches to `inbox` instead of
//           showing an error.
//   create  Email + password + the name on the cover (+ the invite code until
//           specs/002 PR 5) → always `inbox`: the API answers every signup the
//           same way, so the screen can't and doesn't say more.
//   inbox   "Check your inbox", with "Send the link again", which uses the
//           email and password still held in this component's state. Leaving
//           the screen unmounts it, and the password goes with it.
type Mode = 'signIn' | 'create' | 'inbox'

// /verify-email hands the confirmed address over in router state, so signing in
// right after confirming needs only the password. Router state is `any`; this
// guard is what makes it safe to read.
function prefilledEmail(state: unknown): string {
  return typeof state === 'object' &&
    state !== null &&
    'email' in state &&
    typeof state.email === 'string'
    ? state.email
    : ''
}

export default function Login() {
  // useNavigate returns a function that changes the URL without a page load. It
  // only works inside a RouterProvider, which is why the screen is a route.
  const navigate = useNavigate()
  const location = useLocation()

  // Each useState returns [current value, setter]. Calling the setter re-renders
  // the component with the new value. The inputs below are *controlled*: React
  // holds the text and the <input> just shows it — which is what makes "keep the
  // email after a 401" free (only the password is cleared).
  const [mode, setMode] = useState<Mode>('signIn')
  const [email, setEmail] = useState(() => prefilledEmail(location.state))
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [inviteCode, setInviteCode] = useState('')
  // null means no message; the <p> isn't rendered at all rather than left empty.
  const [message, setMessage] = useState<string | null>(null)
  // Disables the buttons while a request is in flight, so a double-tap can't
  // spend two of the ten-per-minute auth rate-limit slots.
  const [submitting, setSubmitting] = useState(false)

  // Arrived here because the session ended while a change was being saved
  // (auth/invalidation.ts): say it may already be saved. Read, not cleared,
  // during render — StrictMode renders twice — and cleared once signed in.
  const [interruptedWrite] = useState(hasInterruptedWrite)

  // The public privacy notice link (FR-001: readable before registering).
  // Shown only once GET /privacy/notice has answered with a notice — a 404
  // means the privacy feature is off (plan.md P25) — and hidden on any error,
  // so signing in never depends on it.
  const [noticeAvailable, setNoticeAvailable] = useState(false)
  useEffect(() => {
    let cancelled = false
    getPrivacyNotice().then(
      (notice) => {
        if (!cancelled) setNoticeAvailable(notice !== null)
      },
      () => {},
    )
    return () => {
      cancelled = true
    }
  }, [])

  // The inbox state replaces the form the user just tapped in, so move focus
  // to its heading: a screen reader announces the new state, and nobody is
  // left focused on a button that no longer exists.
  const inboxHeadingRef = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    if (mode === 'inbox') inboxHeadingRef.current?.focus()
  }, [mode])

  function switchMode(next: Mode) {
    setMode(next)
    setMessage(null)
    // The password typed for one action isn't carried into another. The
    // address is: it's the same person either way.
    setPassword('')
  }

  async function signIn() {
    setMessage(null)
    setSubmitting(true)
    try {
      const { token } = await login({ email, password })
      setToken(token)
      clearInterruptedWrite()
      void navigate('/')
    } catch (err) {
      if (isEmailNotVerified(err)) {
        // Keep the password: "Send the link again" needs it.
        setMode('inbox')
        return
      }
      // `err` is `unknown` in a catch — TypeScript won't assume it's an Error —
      // which is why describeAuthError takes unknown and does the instanceof.
      setMessage(describeAuthError(err))
      setPassword('')
    } finally {
      setSubmitting(false)
    }
  }

  async function createAccount() {
    setMessage(null)
    setSubmitting(true)
    try {
      await register({ email, password, displayName, inviteCode })
      setMode('inbox')
    } catch (err) {
      setMessage(describeAuthError(err))
    } finally {
      setSubmitting(false)
    }
  }

  async function resend() {
    setMessage(null)
    setSubmitting(true)
    try {
      await resendVerification({ email, password })
      // The API answers the same whether or not it sent anything, so this
      // can't promise more than "if it applies, it's on its way".
      setMessage('Sent again. It can take a minute to arrive.')
    } catch (err) {
      setMessage(describeAuthError(err))
    } finally {
      setSubmitting(false)
    }
  }

  // `&&` rendering: when message is null the expression is null and React
  // renders nothing. role="alert" makes a screen reader announce it when it
  // appears. Inline prose under the buttons, not a toast (spec).
  const messageLine = message !== null && (
    <p className="form-message" role="alert">
      {message}
    </p>
  )

  // Shared by both forms. type="email" brings the email keyboard and the
  // browser's own format check before a request is spent; the backend's loose
  // check stays as the backstop. autoComplete="username" because, to a password
  // manager, the address *is* the username.
  const emailField = (
    <label className="field">
      <span className="label">Email</span>
      <input
        className="input"
        type="email"
        inputMode="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        autoComplete="username"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        maxLength={254}
        required
      />
    </label>
  )

  return (
    <main className="page">
      <p className="kicker">Training log</p>
      <h1>
        Gym
        <br />
        Notebook
      </h1>
      <div className="accent-rule" />

      {mode === 'inbox' ? (
        <section className="login-inbox" aria-labelledby="inbox-heading">
          <h2 id="inbox-heading" ref={inboxHeadingRef} tabIndex={-1}>
            Check your inbox
          </h2>
          <p>
            We&apos;ve sent a link to <strong>{email}</strong>. Open it to
            confirm your address, then sign in. The link works for 48 hours.
          </p>
          <p className="muted">
            Nothing there? Check the spam folder, or send it again.
          </p>
          <button
            className="btn btn-secondary btn-block"
            type="button"
            disabled={submitting}
            onClick={() => void resend()}
          >
            Send the link again
          </button>
          {messageLine}
          <p className="muted login-switch">
            <button
              className="btn btn-ghost"
              type="button"
              onClick={() => switchMode('signIn')}
            >
              Back to sign in
            </button>
          </p>
        </section>
      ) : (
        <>
          <p className="muted login-intro">
            {mode === 'signIn'
              ? 'Sign in with your email to open your notebook.'
              : 'Create your notebook. An invite code is required for now.'}
          </p>
          {interruptedWrite && (
            <p className="form-message" role="status">
              {INTERRUPTED_WRITE_NOTICE}
            </p>
          )}

          {/* preventDefault stops the browser's own submit — a full-page GET
              with the fields in the query string. `void` on the async call
              tells the no-floating-promises lint rule the promise is
              deliberately not awaited: the handler can't be async itself,
              React ignores what it returns. The key remounts the form per
              mode, so the browser's autofill sees two distinct forms. */}
          <form
            key={mode}
            className="form-stack"
            onSubmit={(e) => {
              e.preventDefault()
              void (mode === 'signIn' ? signIn() : createAccount())
            }}
          >
            {emailField}
            <label className="field">
              <span className="label">Password</span>
              <input
                className="input"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete={
                  mode === 'signIn' ? 'current-password' : 'new-password'
                }
                required
              />
            </label>

            {mode === 'create' && (
              <>
                <label className="field">
                  <span className="label">Name on the cover</span>
                  <input
                    className="input"
                    type="text"
                    value={displayName}
                    onChange={(e) => setDisplayName(e.target.value)}
                    autoComplete="nickname"
                    maxLength={50}
                    required
                  />
                </label>
                <label className="field">
                  <span className="label">Invite code</span>
                  <input
                    className="input num"
                    type="text"
                    value={inviteCode}
                    onChange={(e) => setInviteCode(e.target.value)}
                    autoComplete="off"
                    autoCapitalize="off"
                    autoCorrect="off"
                    spellCheck={false}
                  />
                </label>
              </>
            )}

            <button
              className="btn btn-primary btn-block login-submit"
              type="submit"
              disabled={submitting}
            >
              {mode === 'signIn' ? 'Sign in' : 'Create account'}
            </button>

            {/* Describe the core service before registration. This is a
                service description, not privacy-notice acknowledgement or
                consent to optional workout details (processing-decision.md
                P1/P2). */}
            {mode === 'create' && (
              <p className="muted login-service">
                Gym Notebook is a free, invite-only training log provided by
                Jouni Uusimaa. Creating an account asks us to keep your private
                record of workout dates, exercises and sets, and show your
                progress chart. Your email address and password let you return
                to that notebook; the address is also where we send the link
                that confirms it.
              </p>
            )}
            {messageLine}
          </form>

          {/* type="button" is load-bearing: these sit outside the form, but a
              <button> defaults to type="submit" wherever it is. */}
          <p className="muted login-switch">
            {mode === 'signIn' ? (
              <>
                New here?{' '}
                <button
                  className="btn btn-ghost"
                  type="button"
                  onClick={() => switchMode('create')}
                >
                  Create an account
                </button>
              </>
            ) : (
              <>
                Already have an account?{' '}
                <button
                  className="btn btn-ghost"
                  type="button"
                  onClick={() => switchMode('signIn')}
                >
                  Sign in
                </button>
              </>
            )}
          </p>
        </>
      )}

      {noticeAvailable && (
        <p className="muted login-privacy">
          <Link to="/privacy" className="btn btn-ghost">
            Privacy notice
          </Link>
        </p>
      )}
    </main>
  )
}
