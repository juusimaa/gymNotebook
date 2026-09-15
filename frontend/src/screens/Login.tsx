import { useState } from 'react'
import { useNavigate } from 'react-router'
import { login, register } from '../api/auth'
import { describeAuthError } from '../api/authErrors'
import { setToken } from '../auth/token'
import './Login.css'

// docs/ui/README.md, screen 1. One form, two actions: "Sign in" is the submit
// button (so Enter in any field signs in), "Create account" is a plain button that
// sends the same fields to /auth/register with the invite code. No mode toggle —
// the spec's field list is the same either way, only the endpoint differs.
type Mode = 'login' | 'register'

export default function Login() {
  // useNavigate returns a function that changes the URL without a page load. It
  // only works inside a RouterProvider, which is why the screen is a route.
  const navigate = useNavigate()

  // Each useState returns [current value, setter]. Calling the setter re-renders
  // the component with the new value. The inputs below are *controlled*: React
  // holds the text and the <input> just shows it — which is what makes "keep the
  // username after a 401" free (only the password is cleared).
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [inviteCode, setInviteCode] = useState('')
  // null means no message; the <p> isn't rendered at all rather than left empty.
  const [message, setMessage] = useState<string | null>(null)
  // Disables both buttons while a request is in flight, so a double-tap can't
  // spend two of the ten-per-minute auth rate-limit slots.
  const [submitting, setSubmitting] = useState(false)

  async function submit(mode: Mode) {
    setMessage(null)
    setSubmitting(true)
    try {
      const { token } =
        mode === 'login'
          ? await login({ username, password })
          : await register({ username, password, inviteCode })
      setToken(token)
      // PR 4 turns "/" into the cover; today it's the placeholder heading.
      void navigate('/')
    } catch (err) {
      // `err` is `unknown` in a catch — TypeScript won't assume it's an Error —
      // which is why describeAuthError takes unknown and does the instanceof.
      setMessage(describeAuthError(err))
      setPassword('')
    } finally {
      setSubmitting(false)
    }
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
      <p className="muted login-intro">
        Sign in to open your notebook. New here? An invite code is required.
      </p>

      {/* preventDefault stops the browser's own submit — a full-page GET with the
          fields in the query string. `void` on the async call tells the
          no-floating-promises lint rule the promise is deliberately not awaited:
          the handler can't be async itself, React ignores what it returns. */}
      <form
        className="form-stack"
        onSubmit={(e) => {
          e.preventDefault()
          void submit('login')
        }}
      >
        {/* <label> wrapping the input associates them without an id, so tapping
            the caption focuses the field. The spec says username, not email:
            plain text, autocapitalisation and autocorrect off. `required` gets
            the browser's empty-field check before a request is spent; the
            backend's 400 stays as the backstop. */}
        <label className="field">
          <span className="label">Username</span>
          <input
            className="input"
            type="text"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            required
          />
        </label>
        <label className="field">
          <span className="label">Password</span>
          <input
            className="input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
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
            placeholder="Optional for existing accounts"
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
          />
        </label>

        <button
          className="btn btn-primary btn-block login-submit"
          type="submit"
          disabled={submitting}
        >
          Sign in
        </button>
        {/* type="button" is load-bearing: a <button> inside a <form> defaults
            to type="submit", so without it this would also fire the login
            handler above. */}
        <p className="muted login-register">
          <button
            className="btn btn-ghost"
            type="button"
            disabled={submitting}
            onClick={() => void submit('register')}
          >
            Create account
          </button>
        </p>

        {/* `&&` rendering: when message is null the expression is null and React
            renders nothing. role="alert" makes a screen reader announce it when
            it appears. Inline prose under the buttons, not a toast (spec). */}
        {message !== null && (
          <p className="form-message" role="alert">
            {message}
          </p>
        )}
      </form>
    </main>
  )
}
