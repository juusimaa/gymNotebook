import { useEffect, useRef, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import {
  login,
  register,
  requestPasswordReset,
  resendVerification,
} from '../api/auth'
import {
  describeAuthError,
  INTERRUPTED_WRITE_NOTICE,
  isCaptchaFailure,
  isEmailNotVerified,
} from '../api/authErrors'
import { ApiError } from '../api/client'
import { getPrivacyNotice } from '../api/privacy'
import {
  clearInterruptedWrite,
  hasInterruptedWrite,
} from '../auth/invalidation'
import { readLoginEntry } from '../auth/loginEntry'
import { setToken } from '../auth/token'
import './Login.css'
import Turnstile from './Turnstile'
import { TURNSTILE_SITE_KEY } from './turnstileSiteKey'

// docs/ui/README.md, screen 1, and specs/002 contracts/ui.md → /login. One
// route, several modes switched in place rather than separate pages, so the
// browser's back button leaves the screen instead of stepping through them:
//
//   signIn  Email + password → the notebook. A 403 email_not_verified (right
//           password, unconfirmed address) switches to `inbox` instead of
//           showing an error.
//   create  Email + password + the name on the cover + the Turnstile bot
//           check → always `inbox`: the API answers every signup the same
//           way, so the screen can't and doesn't say more.
//   forgot  Email + the bot check → always `resetSent`, for the same reason
//           as create: the API answers every reset request the same way.
//
// and two result states that replace the form:
//
//   inbox      "Check your inbox", with "Send the link again", which uses the
//              email and password still held in this component's state.
//              Leaving the screen unmounts it, and the password goes with it.
//   resetSent  "If there's an account for this address, a link is on its way."
type Mode = 'signIn' | 'create' | 'forgot' | 'inbox' | 'resetSent'

// Shown when a request fails in Forgot mode. describeAuthError's 400 mentions a
// password, which this form doesn't have — but a 400 captcha is about the bot
// check, not the address, and keeps describeAuthError's copy.
function describeResetRequestError(err: unknown): string {
  return err instanceof ApiError && err.status === 400 && !isCaptchaFailure(err)
    ? 'Check the email address'
    : describeAuthError(err)
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
  // How the screen was opened (auth/loginEntry.ts): plain sign-in, or with an
  // address or a mode handed over by /verify-email or /reset-password. Read once.
  const [entry] = useState(() => readLoginEntry(location.state))
  const [mode, setMode] = useState<Mode>(entry.mode)
  const [email, setEmail] = useState(entry.email)
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  // The Turnstile token for the next create/forgot submit, null until the
  // widget has one. Single-use, so every attempt bumps captchaRound, which
  // remounts the widget (its `key`) for a fresh one.
  const [captchaToken, setCaptchaToken] = useState<string | null>(null)
  const [captchaRound, setCaptchaRound] = useState(0)
  // null means no message; the <p> isn't rendered at all rather than left empty.
  // Starts as the entry notice ("that reset link has expired"), if any; like
  // any message, it goes once the user moves on.
  // isStatus marks news that isn't a failure ("Sent again"), which is set in
  // ink rather than the error colour.
  const [message, setMessageState] = useState<{
    text: string
    isStatus: boolean
  } | null>(
    entry.notice === null ? null : { text: entry.notice, isStatus: false },
  )
  function setMessage(text: string | null) {
    setMessageState(text === null ? null : { text, isStatus: false })
  }
  function showStatus(text: string) {
    setMessageState({ text, isStatus: true })
  }
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

  // A result state replaces the form the user just tapped in, so move focus to
  // its heading: a screen reader announces the new state, and nobody is left
  // focused on a button that no longer exists.
  const resultHeadingRef = useRef<HTMLHeadingElement>(null)
  // A failed sign-in clears the password and puts the caret back in it, so
  // the retry starts where it has to rather than on the page body.
  const passwordRef = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (mode === 'inbox' || mode === 'resetSent') {
      resultHeadingRef.current?.focus()
    }
  }, [mode])

  // Only the two forms that make the API email an address nobody has proven,
  // and only when a site key switches the check on.
  const needsCaptcha =
    TURNSTILE_SITE_KEY !== '' && (mode === 'create' || mode === 'forgot')

  // After a create or forgot attempt, whatever its outcome: the token has been
  // spent, so drop it and ask the widget for another.
  function renewCaptcha() {
    setCaptchaToken(null)
    setCaptchaRound((round) => round + 1)
  }

  function switchMode(next: Mode) {
    setMode(next)
    setMessage(null)
    // The form remounts per mode (its `key`), and the widget with it.
    setCaptchaToken(null)
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
      passwordRef.current?.focus()
    } finally {
      setSubmitting(false)
    }
  }

  async function createAccount() {
    setMessage(null)
    setSubmitting(true)
    try {
      // `?? undefined` leaves the field out of the JSON when there's no token
      // (the check is off), rather than sending null.
      await register({
        email,
        password,
        displayName,
        turnstileToken: captchaToken ?? undefined,
      })
      setMode('inbox')
    } catch (err) {
      setMessage(describeAuthError(err))
    } finally {
      setSubmitting(false)
      renewCaptcha()
    }
  }

  async function sendResetLink() {
    setMessage(null)
    setSubmitting(true)
    try {
      await requestPasswordReset({
        email,
        turnstileToken: captchaToken ?? undefined,
      })
      setMode('resetSent')
    } catch (err) {
      setMessage(describeResetRequestError(err))
    } finally {
      setSubmitting(false)
      renewCaptcha()
    }
  }

  async function resend() {
    setMessage(null)
    setSubmitting(true)
    try {
      await resendVerification({ email, password })
      // The API answers the same whether or not it sent anything, so this
      // can't promise more than "if it applies, it's on its way".
      showStatus('Sent again. It can take a minute to arrive.')
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
    <p
      className={`form-message${message.isStatus ? ' is-status' : ''}`}
      role={message.isStatus ? 'status' : 'alert'}
    >
      {message.text}
    </p>
  )

  // Shared by all three forms. type="email" brings the email keyboard and the
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
          <h2 id="inbox-heading" ref={resultHeadingRef} tabIndex={-1}>
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
      ) : mode === 'resetSent' ? (
        <section className="login-inbox" aria-labelledby="reset-sent-heading">
          <h2 id="reset-sent-heading" ref={resultHeadingRef} tabIndex={-1}>
            Check your inbox
          </h2>
          {/* "If": the API answers the same whether or not the address has
              an account (FR-013), so the screen can't know either. */}
          <p>
            If there&apos;s an account for <strong>{email}</strong>, we&apos;ve
            sent a link to choose a new password. It works for one hour.
          </p>
          <p className="muted">
            Nothing there? Check the spam folder, or ask for a new link.
          </p>
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
          <p className="muted login-intro">{INTROS[mode]}</p>
          {interruptedWrite && (
            <p className="form-message is-status" role="status">
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
              void (mode === 'signIn'
                ? signIn()
                : mode === 'create'
                  ? createAccount()
                  : sendResetLink())
            }}
          >
            {emailField}
            {mode !== 'forgot' && (
              <label className="field">
                <span className="label">Password</span>
                <input
                  ref={passwordRef}
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
            )}
            {/* Right under the field it's about (contracts/ui.md). Left-aligned
                and small, so it reads as a hint rather than a second action
                competing with "Sign in". */}
            {mode === 'signIn' && (
              <p className="muted login-forgot">
                <button
                  className="btn btn-ghost"
                  type="button"
                  onClick={() => switchMode('forgot')}
                >
                  Forgot your password?
                </button>
              </p>
            )}

            {mode === 'create' && (
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
            )}

            {needsCaptcha && (
              <Turnstile
                key={captchaRound}
                action={mode === 'create' ? 'signup' : 'password_reset'}
                onToken={setCaptchaToken}
              />
            )}

            {/* Held until the bot check has a token, rather than sending a
                request the API is certain to refuse. */}
            <button
              className="btn btn-primary btn-block login-submit"
              type="submit"
              disabled={submitting || (needsCaptcha && captchaToken === null)}
            >
              {SUBMIT_LABELS[mode]}
            </button>

            {/* Describe the core service before registration. This is a
                service description, not privacy-notice acknowledgement or
                consent to optional workout details (processing-decision.md
                P1/P2). */}
            {mode === 'create' && (
              <p className="muted login-service">
                Gym Notebook is a free training log provided by Jouni Uusimaa.
                Creating an account asks us to keep your private record of
                workout dates, exercises and sets, and show your progress chart.
                Your email address and password let you return to that notebook;
                the address is also where we send the link that confirms it.
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
            ) : mode === 'forgot' ? (
              <>
                Remembered it?{' '}
                <button
                  className="btn btn-ghost"
                  type="button"
                  onClick={() => switchMode('signIn')}
                >
                  Sign in
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

// The three form modes' copy, as lookup tables rather than nested ternaries.
type FormMode = 'signIn' | 'create' | 'forgot'

const INTROS: Record<FormMode, string> = {
  signIn: 'Sign in with your email to open your notebook.',
  create: 'Create your notebook.',
  forgot:
    "Enter the address you signed up with and we'll send a link to choose a new password.",
}

const SUBMIT_LABELS: Record<FormMode, string> = {
  signIn: 'Sign in',
  create: 'Create account',
  forgot: 'Send link',
}
