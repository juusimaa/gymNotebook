import { useNavigate, useRouteLoaderData, Link } from 'react-router'
import type { MeResponse } from '../api/auth'
import { clearToken } from '../auth/token'
import './Cover.css'

// docs/ui/README.md, screen 2: the closed cover of the book. Deliberately carries
// no data beyond the owner's name — its job is to make opening the log a decision.
// The two nested hairlines (.cover-frame) are what make it read as a cover.
export default function Cover() {
  const navigate = useNavigate()
  // The router can't know which loader 'auth' names, so this comes back as
  // unknown. The cast is the same trade as `as T` in client.ts: we know the id
  // maps to requireAuth, which returns a MeResponse. A wrong id would give
  // undefined here and a crash on .username — so this line is where the coupling
  // to routes.tsx lives.
  const user = useRouteLoaderData('auth') as MeResponse

  // Client-side only (PLAN.md, Auth): there's nothing to revoke server-side
  // short of a password change, so dropping the token is the whole sign-out.
  function signOut() {
    clearToken()
    void navigate('/login')
  }

  return (
    <main className="page cover">
      <div className="cover-frame" />
      <div className="cover-frame cover-frame-inner" />

      <div className="cover-body">
        <p className="kicker">Training log</p>
        <h1 className="cover-title">
          Gym
          <br />
          Notebook
        </h1>
        <div className="cover-ornament" aria-hidden="true">
          <span className="cover-ornament-rule" />
          <span className="cover-ornament-diamond" />
          <span className="cover-ornament-rule" />
        </div>
        <p className="cover-owner">{user.username}</p>
        <p className="cover-volume num">
          Volume I · {new Date().getFullYear()}
        </p>
      </div>

      <Link
        to="/workouts"
        className="btn btn-primary btn-block cover-open-notebook"
      >
        Open the notebook
      </Link>
      <div className="cover-actions">
        <Link to="/change-password" className="btn btn-ghost">
          Change password
        </Link>
        <button type="button" className="btn btn-ghost" onClick={signOut}>
          Sign out
        </button>
      </div>
      <p className="cover-support">
        Enjoying the notebook?{' '}
        <a
          href="https://buymeacoffee.com/jouni"
          target="_blank"
          rel="noreferrer"
        >
          Buy me a coffee ↗
        </a>
      </p>
    </main>
  )
}
