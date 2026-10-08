import { useEffect, useState } from 'react'
import { useNavigate, useRouteLoaderData, Link } from 'react-router'
import type { MeResponse } from '../api/auth'
import { getAccountPrivacy } from '../api/privacy'
import { clearToken } from '../auth/token'
import { clearEditorDrafts } from './editorDraftStorage'
import './Cover.css'

// docs/ui/README.md, screen 2: the closed cover of the book. Deliberately carries
// no data beyond the owner's name — its job is to make opening the log a decision —
// and, small beside the account links, the address this browser is signed in
// with (specs/002 contracts/ui.md → Cover), so a shared phone can't leave anyone
// unsure whose notebook this is.
// The two nested hairlines (.cover-frame) are what make it read as a cover.
export default function Cover() {
  const navigate = useNavigate()
  // The router can't know which loader 'auth' names, so this comes back as
  // unknown. The cast is the same trade as `as T` in client.ts: we know the id
  // maps to requireAuth, which returns a MeResponse. A wrong id would give
  // undefined here and a crash on .displayName — so this line is where the coupling
  // to routes.tsx lives.
  const user = useRouteLoaderData('auth') as MeResponse

  // "Privacy & account" appears only when the privacy feature is on, which the
  // frontend learns from the backend: a 404 from GET /account/privacy means off
  // (plan.md P25). Hidden while loading and on any error — the cover must never
  // fail to open over an optional link. A 401 is left to the route guard.
  const [showPrivacy, setShowPrivacy] = useState(false)
  useEffect(() => {
    let cancelled = false
    getAccountPrivacy().then(
      (state) => {
        if (!cancelled) setShowPrivacy(state !== null)
      },
      () => {},
    )
    return () => {
      cancelled = true
    }
  }, [])

  // Client-side only (PLAN.md, Auth): there's nothing to revoke server-side
  // short of a password change, so dropping the token is the whole sign-out.
  function signOut() {
    clearToken()
    // A deliberate sign-out leaves no draft behind in this tab.
    clearEditorDrafts()
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
        <p className="cover-owner">{user.displayName}</p>
        <p className="cover-volume num">
          Volume I · {new Date().getFullYear()}
        </p>
      </div>

      <Link to="/workouts" className="btn btn-primary btn-block">
        Open the notebook
      </Link>
      <p className="cover-account">
        Signed in as <span className="cover-account-email">{user.email}</span>
      </p>
      <div className="cover-actions">
        <Link to="/change-password" className="btn btn-ghost">
          Change password
        </Link>
        <button type="button" className="btn btn-ghost" onClick={signOut}>
          Sign out
        </button>
      </div>
      {/* Always shown: backup isn't part of the privacy feature (specs/004
          D2). The last-backup date stays on its own screen (plan Q5). */}
      <p className="cover-line">
        <Link to="/backup" className="btn btn-ghost">
          Backup &amp; restore
        </Link>
      </p>
      {showPrivacy && (
        <p className="cover-line">
          <Link to="/account/privacy" className="btn btn-ghost">
            Privacy &amp; account
          </Link>
        </p>
      )}
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
