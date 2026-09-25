import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { ApiError } from '../api/client'
import { revokePendingDownloads, saveBlob } from '../api/download'
import { EXPORT_FILE_NAME, exportNotebook } from '../api/privacy'
import { clearToken } from '../auth/token'
import './Privacy.css'

// /account/export — "Take a copy" (specs/001 user story 3, contracts/ui.md →
// Export transitions). Under the auth guard but outside the notice gate, like
// the rest of Privacy & account. The states:
//
//   idle → verifying (password sent) → receiving (file arriving) → complete
//
// with a recoverable failure from any of them. The password is cleared as soon
// as it has been sent and never stored; a retry asks for it again, and gets a
// fresh snapshot. Only a complete file is saved (api/download.ts).

type Phase = 'idle' | 'verifying' | 'receiving' | 'complete'

// What went wrong, in words, or null when the error needs no message (the user
// cancelled). The 401 is handled before this: it signs the user out instead.
function describeError(err: unknown): string | null {
  if (err instanceof DOMException && err.name === 'AbortError') {
    return null
  }
  if (err instanceof ApiError) {
    if (err.status === 400) {
      return err.code === 'password_verification_failed'
        ? 'Check your current password and try again.'
        : 'Enter your current password.'
    }
    if (err.status === 429) {
      return err.code === 'export_in_progress'
        ? 'An export of your notebook is already running. Wait for it to finish, then try again.'
        : 'Too many attempts. Wait a minute and try again.'
    }
    if (err.status === 503) {
      return 'The notebook is busy for a moment. Nothing has changed. Try again in a few seconds.'
    }
  }
  // A broken connection, a cut-short file or an unexpected status: the file
  // wasn't saved, and an export never changes the notebook.
  return "The export didn't finish, so no file was saved. Your notebook has not changed. Try again."
}

export default function ExportData() {
  const navigate = useNavigate()
  const [password, setPassword] = useState('')
  const [phase, setPhase] = useState<Phase>('idle')
  const [message, setMessage] = useState<string | null>(null)
  const pending = useRef<AbortController | null>(null)
  const unmounted = useRef(false)
  const passwordRef = useRef<HTMLInputElement>(null)

  // Leaving the screen (including signing out) cancels a download in flight and
  // releases any object URL still held for a saved file.
  useEffect(() => {
    unmounted.current = false
    return () => {
      unmounted.current = true
      pending.current?.abort()
      revokePendingDownloads()
    }
  }, [])

  // After a failure, back to the password field, where the next attempt starts.
  // An effect, because the field is only enabled again once this has rendered.
  useEffect(() => {
    if (message !== null) passwordRef.current?.focus()
  }, [message])

  async function submit() {
    const controller = new AbortController()
    pending.current = controller
    const typed = password
    setPassword('')
    setMessage(null)
    setPhase('verifying')
    try {
      const file = await exportNotebook(typed, {
        signal: controller.signal,
        onReceiving: () => setPhase('receiving'),
      })
      saveBlob(file, EXPORT_FILE_NAME)
      setPhase('complete')
    } catch (err) {
      if (unmounted.current) {
        return // the screen is gone; nothing left to update
      }
      // The session no longer works (signed out elsewhere, password changed,
      // account removed): same handling as the route guard's.
      if (err instanceof ApiError && err.status === 401) {
        clearToken()
        void navigate('/login')
        return
      }
      setPhase('idle')
      setMessage(describeError(err))
    } finally {
      pending.current = null
    }
  }

  function cancel() {
    pending.current?.abort()
  }

  const busy = phase === 'verifying' || phase === 'receiving'

  return (
    <main className="page">
      <p className="privacy-back">
        <Link to="/account/privacy" className="btn btn-ghost">
          ← Privacy &amp; account
        </Link>
      </p>
      <p className="kicker">Your information</p>
      <h2 className="privacy-heading">Take a copy</h2>
      <p className="export-intro">
        Download one JSON file containing your account details, exercises,
        sessions, notes and sets. The file explains its fields and keeps each
        exercise&apos;s place in a session.
      </p>

      <form
        className="form-stack export-form"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        <label className="field">
          <span className="label">Current password</span>
          <input
            ref={passwordRef}
            className="input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            disabled={busy}
            required
          />
        </label>
        <p className="muted export-note">
          The file downloads to your device and contains personal information.
          Keep it somewhere private.
        </p>

        {message !== null && (
          <p className="form-message" role="alert">
            {message}
          </p>
        )}

        {/* One live region for progress and completion, announced politely. */}
        <div role="status" aria-live="polite">
          {phase === 'verifying' && (
            <p className="export-progress">Checking your password…</p>
          )}
          {phase === 'receiving' && (
            <>
              <p className="export-progress">Preparing your file…</p>
              {/* No value: the size isn't known until the file is complete. */}
              <progress
                className="export-progress-bar"
                aria-label="Export progress"
              />
            </>
          )}
          {phase === 'complete' && (
            <p className="export-done">
              Your file has been saved as {EXPORT_FILE_NAME}. Your notebook has
              not changed.
            </p>
          )}
        </div>

        {busy ? (
          <button
            className="btn btn-secondary btn-block"
            type="button"
            onClick={cancel}
          >
            Cancel
          </button>
        ) : (
          <button className="btn btn-primary btn-block" type="submit">
            {phase === 'complete' ? 'Download again' : 'Download my data'}
          </button>
        )}
      </form>
    </main>
  )
}
