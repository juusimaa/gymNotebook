import { useEffect, useRef, useState } from 'react'
import { useNavigate, useRouteLoaderData, Link } from 'react-router'
import type { MeResponse } from '../api/auth'
import { ApiError } from '../api/client'
import { resetDemo } from '../api/demo'
import { getAccountPrivacy, type AccountPrivacyState } from '../api/privacy'
import { optionalDetailsAllowed } from '../auth/noticeGate'
import { clearToken } from '../auth/token'
import { describeDemoResetError, formatDemoEnd } from './demoFormat'
import { clearEditorDrafts } from './editorDraftStorage'
import './Cover.css'

// docs/ui/README.md, screen 2: the closed cover of the book. Deliberately carries
// no data beyond the owner's name — its job is to make opening the log a decision —
// and, small beside the account links, the address this browser is signed in
// with (specs/002 contracts/ui.md → Cover), so a shared phone can't leave anyone
// unsure whose notebook this is.
// The two nested hairlines (.cover-frame) are what make it read as a cover.
//
// A demo account's cover (specs/007, docs/ui/README.md → screen 2) says when
// the demo ends, never shows the internal "demo:…" address, and swaps Change
// password for Reset demo data and Sign out for Leave demo, each asked about
// in an inline confirmation rather than a browser dialog.
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
  // undefined while loading or after an error; null when the feature is off.
  // A demo's reset hint also reads the consent from it.
  const [privacy, setPrivacy] = useState<AccountPrivacyState | null>()
  const showPrivacy = privacy != null
  useEffect(() => {
    let cancelled = false
    getAccountPrivacy().then(
      (state) => {
        if (!cancelled) setPrivacy(state)
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

  const demoEnd =
    user.demoExpiresAt === null
      ? null
      : formatDemoEnd(user.demoExpiresAt, new Date())

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
        {demoEnd !== null && (
          <p className="cover-demo-badge num">Demo · ends {demoEnd}</p>
        )}
      </div>

      <Link to="/workouts" className="btn btn-primary btn-block">
        Open the notebook
      </Link>
      {/* The account's chores, apart from the notebook under a hairline and
        one quiet heading, so "Open the notebook" stands alone as the
        cover's decision (design critique 2026-10-16, second pass). Sign out
        comes last. Backup is always shown: it isn't part of the privacy
        feature (specs/004 D2), and the last-backup date stays on its own
        screen (plan Q5). */}
      <section
        className="cover-account-group"
        aria-labelledby="cover-account-heading"
      >
        <h2 id="cover-account-heading" className="cover-account-heading">
          Account
        </h2>
        {demoEnd === null ? (
          <>
            <p className="cover-account">
              Signed in as{' '}
              <span className="cover-account-email">{user.email}</span>
            </p>
            <div className="cover-actions">
              <Link to="/change-password" className="btn btn-ghost">
                Change password
              </Link>
              <Link to="/backup" className="btn btn-ghost">
                Backup &amp; restore
              </Link>
              {showPrivacy && (
                <Link to="/account/privacy" className="btn btn-ghost">
                  Privacy &amp; account
                </Link>
              )}
              <button type="button" className="btn btn-ghost" onClick={signOut}>
                Sign out
              </button>
            </div>
          </>
        ) : (
          <DemoAccount
            demoEnd={demoEnd}
            showPrivacy={showPrivacy}
            // Only with the feature on and consent not given: with the
            // feature off, a reset seeds the details anyway.
            detailsHint={privacy != null && !optionalDetailsAllowed(privacy)}
            onLeave={signOut}
          />
        )}
      </section>
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

type DemoStep = 'idle' | 'reset' | 'leave'

// The Account group of a demo's cover: what it is, its four actions, and the
// two confirmations that take their place. Its own component so the
// registered account's cover above stays as it was.
function DemoAccount({
  demoEnd,
  showPrivacy,
  detailsHint,
  onLeave,
}: {
  demoEnd: string
  showPrivacy: boolean
  detailsHint: boolean
  onLeave: () => void
}) {
  const [step, setStep] = useState<DemoStep>('idle')
  const [resetting, setResetting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // "Demo data reset…", shown once the reset is committed.
  const [notice, setNotice] = useState<string | null>(null)

  // Focus follows the step: into an opened confirmation's heading, so a
  // screen reader reads the question, and back to the action that opened it
  // on Cancel or Stay, so nobody is left on a button that no longer exists.
  const confirmHeadingRef = useRef<HTMLHeadingElement>(null)
  const resetButtonRef = useRef<HTMLButtonElement>(null)
  const leaveButtonRef = useRef<HTMLButtonElement>(null)
  const returnFocusTo = useRef<'reset' | 'leave' | null>(null)
  useEffect(() => {
    if (step !== 'idle') {
      confirmHeadingRef.current?.focus()
    } else if (returnFocusTo.current !== null) {
      ;(returnFocusTo.current === 'reset'
        ? resetButtonRef
        : leaveButtonRef
      ).current?.focus()
      returnFocusTo.current = null
    }
  }, [step])

  function open(next: 'reset' | 'leave') {
    setStep(next)
    setError(null)
    setNotice(null)
  }

  function cancel() {
    returnFocusTo.current = step === 'leave' ? 'leave' : 'reset'
    setStep('idle')
    setError(null)
  }

  async function reset() {
    setError(null)
    setResetting(true)
    try {
      await resetDemo()
      // A draft left in this tab belonged to the notebook that is now gone.
      clearEditorDrafts()
      returnFocusTo.current = 'reset'
      setStep('idle')
      setNotice('Demo data reset. The notebook is new again.')
    } catch (err) {
      // A 401 means the demo has ended: api/client.ts has already signed out.
      if (err instanceof ApiError && err.status === 401) return
      setError(describeDemoResetError(err))
    } finally {
      setResetting(false)
    }
  }

  return (
    <>
      <p className="cover-account">
        A demo notebook, deleted when the demo ends
      </p>
      {notice !== null && (
        <p className="cover-account cover-demo-notice" role="status">
          {notice}
        </p>
      )}

      {step === 'idle' && (
        <div className="cover-actions">
          <button
            ref={resetButtonRef}
            type="button"
            className="btn btn-ghost"
            onClick={() => open('reset')}
          >
            Reset demo data
          </button>
          <Link to="/backup" className="btn btn-ghost">
            Backup &amp; restore
          </Link>
          {showPrivacy && (
            <Link to="/account/privacy" className="btn btn-ghost">
              Privacy &amp; account
            </Link>
          )}
          <button
            ref={leaveButtonRef}
            type="button"
            className="btn btn-ghost"
            onClick={() => open('leave')}
          >
            Leave demo
          </button>
        </div>
      )}

      {step === 'reset' && (
        <section
          className="cover-demo-confirm"
          aria-labelledby="cover-demo-reset-heading"
        >
          <h3
            id="cover-demo-reset-heading"
            ref={confirmHeadingRef}
            tabIndex={-1}
          >
            Reset demo data?
          </h3>
          <p>
            Every session, template and exercise in this demo is replaced with a
            new sample notebook. What you changed or added is lost. The demo
            still ends {demoEnd}.
          </p>
          {detailsHint && (
            <p className="muted">
              Sample titles, gyms, notes and bodyweight are added only after you
              allow optional details in Privacy &amp; account. Allow them first,
              then reset.
            </p>
          )}
          {error !== null && (
            <p className="form-message" role="alert">
              {error}
            </p>
          )}
          <div className="cover-demo-confirm-actions">
            <button
              type="button"
              className="btn btn-secondary"
              disabled={resetting}
              onClick={() => void reset()}
            >
              {resetting ? 'Resetting…' : 'Reset demo data'}
            </button>
            <button
              type="button"
              className="btn btn-ghost"
              disabled={resetting}
              onClick={cancel}
            >
              Cancel
            </button>
          </div>
        </section>
      )}

      {step === 'leave' && (
        <section
          className="cover-demo-confirm"
          aria-labelledby="cover-demo-leave-heading"
        >
          <h3
            id="cover-demo-leave-heading"
            ref={confirmHeadingRef}
            tabIndex={-1}
          >
            Leave the demo?
          </h3>
          <p>
            You can&apos;t come back to this demo; it has no password. It is
            deleted when it ends. Trying the demo again starts a new one.
          </p>
          <div className="cover-demo-confirm-actions">
            <button
              type="button"
              className="btn btn-secondary"
              onClick={onLeave}
            >
              Leave demo
            </button>
            <button type="button" className="btn btn-ghost" onClick={cancel}>
              Stay
            </button>
          </div>
        </section>
      )}
    </>
  )
}
