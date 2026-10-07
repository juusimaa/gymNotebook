import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { verifyEmail } from '../api/auth'
import { describeAuthError } from '../api/authErrors'
import {
  linkFailure,
  readFragmentToken,
  removeFragmentFromUrl,
} from '../auth/linkToken'
import { getToken, isTokenExpired } from '../auth/token'
import './Login.css'

// /verify-email#token=… (specs/002 contracts/ui.md). Public: the link is often
// opened in a different browser from the one the app runs in — a mail app's
// in-app browser, or Safari when the app lives on the home screen — so
// confirming never depends on a session (spec Edge Cases).
//
// The token is read once from the fragment and removed from the address bar
// before anything else happens. It's posted from script, not confirmed by
// opening the link, so a mail scanner that fetches links without running
// script confirms nothing.
type State =
  | { kind: 'working' }
  | { kind: 'confirmed'; email: string }
  | { kind: 'expired' }
  | { kind: 'invalid' }
  // The server couldn't be asked (offline, 429, 5xx): not a verdict on the
  // link, so offer to try again with the token still held in memory.
  | { kind: 'failed'; message: string }

// Sends the token and reports the outcome. Outside the component so the effect
// below can call it without listing it as a dependency. Results are set even if
// the component has since unmounted (React ignores that), which matters under
// StrictMode: its dev-only unmount/remount would otherwise drop the one
// request's answer.
async function confirm(linkToken: string, setState: (state: State) => void) {
  setState({ kind: 'working' })
  try {
    const { email } = await verifyEmail(linkToken)
    setState({ kind: 'confirmed', email })
  } catch (err) {
    const failure = linkFailure(err)
    setState(
      failure === null
        ? { kind: 'failed', message: describeAuthError(err) }
        : { kind: failure },
    )
  }
}

export default function VerifyEmail() {
  const navigate = useNavigate()

  // Read during the first render — the lazy initializer runs once — so the
  // token is captured before the effect below strips it from the URL.
  const [token] = useState(() => readFragmentToken(window.location.hash))
  const [state, setState] = useState<State>(
    token === null ? { kind: 'invalid' } : { kind: 'working' },
  )

  // Once per page load. The ref, not the effect's dependency list, is what
  // stops StrictMode's second run from posting the token twice; a double post
  // would be harmless (confirming is idempotent) but spends the rate limit.
  const started = useRef(false)
  useEffect(() => {
    removeFragmentFromUrl()
    if (started.current || token === null) return
    started.current = true
    void confirm(token, setState)
  }, [token])

  const headingRef = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    if (state.kind !== 'working') headingRef.current?.focus()
  }, [state.kind])

  // A session already in this browser: the way on is the notebook itself, not
  // another sign-in. Expired tokens don't count — the guard would only bounce
  // them to /login.
  const sessionToken = getToken()
  const signedIn = sessionToken !== null && !isTokenExpired(sessionToken)

  function goToSignIn(email?: string) {
    void navigate('/login', email === undefined ? {} : { state: { email } })
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

      <section className="login-inbox" aria-live="polite">
        {state.kind === 'working' && <p className="muted">Confirming…</p>}

        {state.kind === 'confirmed' && (
          <>
            <h2 ref={headingRef} tabIndex={-1}>
              Address confirmed
            </h2>
            <p>
              <strong>{state.email}</strong> is confirmed.
            </p>
            {signedIn ? (
              <Link to="/" className="btn btn-primary btn-block">
                Open my notebook
              </Link>
            ) : (
              <button
                type="button"
                className="btn btn-primary btn-block"
                onClick={() => goToSignIn(state.email)}
              >
                Sign in
              </button>
            )}
          </>
        )}

        {state.kind === 'expired' && (
          <>
            <h2 ref={headingRef} tabIndex={-1}>
              This link has expired
            </h2>
            <p>
              Confirmation links work for 48 hours. Sign in with your email and
              password, and you can have a new link sent.
            </p>
            <button
              type="button"
              className="btn btn-primary btn-block"
              onClick={() => goToSignIn()}
            >
              Go to sign in
            </button>
          </>
        )}

        {state.kind === 'invalid' && (
          <>
            <h2 ref={headingRef} tabIndex={-1}>
              This link doesn&apos;t work
            </h2>
            <p>
              It may have been copied only in part. Open it again from the
              email, or sign in to have a new one sent.
            </p>
            <button
              type="button"
              className="btn btn-primary btn-block"
              onClick={() => goToSignIn()}
            >
              Go to sign in
            </button>
          </>
        )}

        {state.kind === 'failed' && token !== null && (
          <>
            <h2 ref={headingRef} tabIndex={-1}>
              Couldn&apos;t confirm yet
            </h2>
            <p className="form-message" role="alert">
              {state.message}
            </p>
            <button
              type="button"
              className="btn btn-primary btn-block"
              onClick={() => void confirm(token, setState)}
            >
              Try again
            </button>
          </>
        )}
      </section>
    </main>
  )
}
