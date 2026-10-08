import { useEffect, useRef, useState, type RefObject } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  backupFileName,
  exportNotebook,
  getLastBackup,
  restoreBackup,
  type BackupFormat,
  type RestoreResult,
} from '../api/backup'
import { ApiError } from '../api/client'
import { revokePendingDownloads, saveBlob } from '../api/download'
import { getAccountPrivacy } from '../api/privacy'
import { clearToken } from '../auth/token'
import { prefersDecimalComma, toCsv } from './backupCsv'
import {
  precheckBackupFile,
  type BackupFile,
  type BackupSummary,
} from './backupFile'
import {
  formatCount,
  formatInstantDate,
  formatWorkoutLongDate,
  formatWorkoutTime,
} from './workoutFormat'
import './Privacy.css'
import './Backup.css'

// /backup — Backup & restore (specs/004 contracts/ui.md). Replaces spec 001's
// "Take a copy" (/account/export now redirects here) and keeps its state
// machine for the download:
//
//   idle → verifying (password sent) → receiving (file arriving) → complete
//
// with a recoverable failure from any of them. Under the auth guard but outside
// the notice gate, and shown whether or not the privacy feature is on (plan D2).
// The restore section below it has its own states (RestoreSection).

type Phase = 'idle' | 'verifying' | 'receiving' | 'complete'

// The line under the heading. `fresh` plays the gold underline once, right
// after this screen's own full backup moved the date.
type LastBackupLine =
  | { status: 'loading' }
  | { status: 'loaded'; at: string | null; fresh: boolean }
  | { status: 'hidden' }

// "3 October 2026, 09.42": the app's long date and HH.mm clock.
function formatMoment(instant: string): string {
  return `${formatInstantDate(instant)}, ${formatWorkoutTime(instant)}`
}

// Grouped thousands in the interface's English: 1,846.
function formatNumber(value: number): string {
  return value.toLocaleString('en-GB')
}

// What went wrong with a backup, in words, or null when the error needs no
// message (the user cancelled). The 401 is handled before this: it signs out.
function describeBackupError(err: unknown): string | null {
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
        ? 'A backup of your notebook is already running. Wait for it to finish, then try again.'
        : 'Too many attempts. Wait a minute and try again.'
    }
    if (err.status === 503) {
      return 'The notebook is busy for a moment. Nothing has changed. Try again in a few seconds.'
    }
  }
  // A broken connection, a cut-short file or an unexpected status: the file
  // wasn't saved, and a backup never changes the notebook.
  return "The backup didn't finish, so no file was saved. Your notebook has not changed. Try again."
}

// The same 401 handling as the route guard's: the session no longer works
// (signed out elsewhere, password changed, account removed).
function isSessionGone(err: unknown): boolean {
  return err instanceof ApiError && err.status === 401
}

export default function Backup() {
  const navigate = useNavigate()
  const [format, setFormat] = useState<BackupFormat>('json')
  const [password, setPassword] = useState('')
  const [phase, setPhase] = useState<Phase>('idle')
  // The file this screen last saved, for the completion line.
  const [saved, setSaved] = useState<{
    name: string
    format: BackupFormat
  } | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  // Whether the message is about the password, which then marks the field.
  const [passwordInvalid, setPasswordInvalid] = useState(false)
  const [lastBackup, setLastBackup] = useState<LastBackupLine>({
    status: 'loading',
  })
  const [showPrivacy, setShowPrivacy] = useState(false)
  const pending = useRef<AbortController | null>(null)
  const unmounted = useRef(false)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const passwordRef = useRef<HTMLInputElement>(null)

  // Leaving the screen (including signing out) cancels a download in flight and
  // releases any object URL still held for a saved file.
  useEffect(() => {
    unmounted.current = false
    headingRef.current?.focus()
    return () => {
      unmounted.current = true
      pending.current?.abort()
      revokePendingDownloads()
    }
  }, [])

  // The last-backup line. A failure hides it rather than showing an error:
  // reporting it isn't this screen's job. A 401 is left to the app-wide
  // handler, which has already signed out.
  useEffect(() => {
    let cancelled = false
    getLastBackup().then(
      ({ lastBackupAt }) => {
        if (!cancelled)
          setLastBackup({ status: 'loaded', at: lastBackupAt, fresh: false })
      },
      () => {
        if (!cancelled) setLastBackup({ status: 'hidden' })
      },
    )
    return () => {
      cancelled = true
    }
  }, [])

  // The Privacy & account link at the foot, only when that feature is on —
  // the same check as the cover's. Hidden while loading and on any error.
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

  // After a failure, back to the password field, where the next attempt starts.
  // An effect, because the field is only enabled again once this has rendered.
  useEffect(() => {
    if (message !== null) passwordRef.current?.focus()
  }, [message])

  function chooseFormat(next: BackupFormat) {
    setFormat(next)
    // A finished or failed download belonged to the other format.
    setPhase('idle')
    setSaved(null)
    setMessage(null)
    setPasswordInvalid(false)
  }

  // The server stamps the last backup before the response ends (plan D6), so
  // by the time the file is complete the new time can be read back. Reading
  // it, rather than assuming "now", keeps a failed stamp honest: the line then
  // still shows the older time.
  async function refreshLastBackup() {
    try {
      const { lastBackupAt } = await getLastBackup()
      if (unmounted.current) return
      setLastBackup((previous) => ({
        status: 'loaded',
        at: lastBackupAt,
        fresh:
          lastBackupAt !== null &&
          (previous.status !== 'loaded' || previous.at !== lastBackupAt),
      }))
    } catch {
      // Leave the line as it was; the backup itself succeeded.
    }
  }

  async function submit() {
    const controller = new AbortController()
    pending.current = controller
    const typed = password
    const chosen = format
    setPassword('')
    setMessage(null)
    setPasswordInvalid(false)
    setSaved(null)
    setPhase('verifying')
    try {
      const file = await exportNotebook(typed, chosen, {
        signal: controller.signal,
        onReceiving: () => setPhase('receiving'),
      })
      const name = backupFileName(chosen)
      if (chosen === 'json') {
        saveBlob(file, name)
      } else {
        // The spreadsheet is written here, from the same complete JSON, in the
        // browser's time zone and decimal convention (plan D4, D5).
        const notebook = JSON.parse(await file.text()) as BackupFile
        const csv = toCsv(notebook, {
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          decimalComma: prefersDecimalComma(navigator.language),
        })
        saveBlob(new Blob([csv], { type: 'text/csv;charset=utf-8' }), name)
      }
      if (unmounted.current) return
      setSaved({ name, format: chosen })
      setPhase('complete')
      if (chosen === 'json') void refreshLastBackup()
    } catch (err) {
      if (unmounted.current) {
        return // the screen is gone; nothing left to update
      }
      if (isSessionGone(err)) {
        clearToken()
        void navigate('/login')
        return
      }
      setPhase('idle')
      setMessage(describeBackupError(err))
      setPasswordInvalid(err instanceof ApiError && err.status === 400)
    } finally {
      pending.current = null
    }
  }

  function cancel() {
    pending.current?.abort()
  }

  const busy = phase === 'verifying' || phase === 'receiving'
  const what = format === 'json' ? 'backup' : 'spreadsheet'

  return (
    <main className="page">
      <p className="privacy-back">
        <Link to="/" className="btn btn-ghost">
          ← Cover
        </Link>
      </p>
      <h1 className="privacy-heading" ref={headingRef} tabIndex={-1}>
        Backup &amp; restore
      </h1>
      {lastBackup.status === 'loading' && (
        <p className="backup-last muted">Checking your last backup…</p>
      )}
      {lastBackup.status === 'loaded' && (
        <p
          // A new key replays the underline when the date moves again.
          key={lastBackup.at ?? 'none'}
          className={`backup-last muted${lastBackup.fresh ? ' is-fresh' : ''}`}
        >
          {lastBackup.at === null ? (
            'No full backup yet.'
          ) : (
            <>
              Last full backup:{' '}
              <strong className="num">{formatMoment(lastBackup.at)}</strong>
            </>
          )}
        </p>
      )}

      <section className="privacy-block" aria-labelledby="backup-make">
        <h2 id="backup-make">Make a backup</h2>
        <form
          className="form-stack"
          onSubmit={(e) => {
            e.preventDefault()
            void submit()
          }}
        >
          <fieldset className="backup-format" disabled={busy}>
            <legend>Format</legend>
            <label className="backup-option">
              <input
                type="radio"
                name="backup-format"
                value="json"
                checked={format === 'json'}
                onChange={() => chooseFormat('json')}
              />
              <span className="backup-option-title">
                Full backup <small>JSON</small>
              </span>
              <span className="backup-option-note">
                Everything in your notebook. This is the file you can restore
                from.
              </span>
            </label>
            <label className="backup-option">
              <input
                type="radio"
                name="backup-format"
                value="csv"
                checked={format === 'csv'}
                onChange={() => chooseFormat('csv')}
              />
              <span className="backup-option-title">
                Spreadsheet <small>CSV</small>
              </span>
              <span className="backup-option-note">
                One row per set, for Excel or Numbers. It can&apos;t be
                restored.
              </span>
            </label>
          </fieldset>

          <label className="field">
            <span className="label">Current password</span>
            <input
              ref={passwordRef}
              className="input"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              aria-invalid={passwordInvalid || undefined}
              disabled={busy}
              required
            />
          </label>
          <p className="muted backup-note">
            The file contains personal information. Keep it somewhere private.
          </p>

          {message !== null && (
            <p className="form-message" role="alert">
              {message}
            </p>
          )}

          {/* One live region for progress and completion, announced politely. */}
          <div className="backup-status" role="status" aria-live="polite">
            {phase === 'verifying' && (
              <p className="backup-line">Checking your password…</p>
            )}
            {phase === 'receiving' && (
              <>
                <p className="backup-line">Preparing your {what}…</p>
                {/* No value: the size isn't known until the file is complete. */}
                <progress
                  className="backup-progress"
                  aria-label="Backup progress"
                />
              </>
            )}
            {phase === 'complete' && saved !== null && (
              <p className="backup-line backup-done">
                Saved as <span className="num">{saved.name}</span>.{' '}
                {saved.format === 'json'
                  ? 'Your notebook has not changed.'
                  : "A spreadsheet can't be restored; keep a full backup too."}
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
              {phase === 'complete'
                ? 'Download again'
                : format === 'json'
                  ? 'Download backup'
                  : 'Download spreadsheet'}
            </button>
          )}
        </form>
      </section>

      <RestoreSection
        unmounted={unmounted}
        onSessionGone={() => {
          clearToken()
          void navigate('/login')
        }}
      />

      {showPrivacy && (
        <p className="backup-foot">
          <Link to="/account/privacy" className="btn btn-ghost">
            Privacy &amp; account
          </Link>
        </p>
      )}
    </main>
  )
}

// Restore (contracts/ui.md → Restore states). The section changes in place,
// never in a modal:
//
//   idle → reading (checked on this device) → summary → restoring → done
//
// and a failure, which keeps the chosen file's name, from reading or
// restoring. Once sent, a restore can't be cancelled: the server completes it
// or rolls it back.
type RestoreState =
  | { step: 'idle' }
  | { step: 'reading'; name: string }
  | {
      step: 'summary'
      name: string
      file: File
      backup: BackupFile
      summary: BackupSummary
    }
  | { step: 'restoring'; name: string; file: File; backup: BackupFile }
  | { step: 'done'; name: string; backup: BackupFile; result: RestoreResult }
  | {
      step: 'failed'
      name: string
      message: string
      // Only a failure that may pass on a second try offers Try again.
      retry: { file: File; backup: BackupFile } | null
    }

const NOT_A_BACKUP =
  "This isn't a Gym Notebook backup. Choose the .json file from Make a backup. Spreadsheets can't be restored."
const NEWER_VERSION =
  'This backup was made by a newer version of Gym Notebook. Reload the app and try again.'
const DAMAGED =
  'This file is damaged or incomplete, so nothing was restored. Try another backup.'
const TOO_LARGE =
  "This file is larger than 25 MB, so it can't be restored here."
const RESTORE_FAILED =
  "The restore didn't finish, so nothing was added. Your notebook has not changed. Try again."

// The same words for a file this device rejected and one the server did: the
// server re-checks everything (FR-012), so either can be the one to say no.
function describeFileProblem(
  // A pre-check failure reason, or the server's `reason`, which uses
  // the same names; anything unrecognised reads as a damaged file.
  reason: string | undefined,
): string {
  switch (reason) {
    case 'too_large':
      return TOO_LARGE
    case 'not_a_backup':
      return NOT_A_BACKUP
    case 'unsupported_version':
      return NEWER_VERSION
    default:
      return DAMAGED
  }
}

// The failure for a rejected upload, and whether trying the same file again
// could help. Null for a 401, which the caller turns into a sign-out.
function describeRestoreError(
  err: unknown,
): { message: string; retryable: boolean } | null {
  if (isSessionGone(err)) return null
  if (err instanceof ApiError) {
    if (err.status === 400 && err.code === 'backup_invalid') {
      return { message: describeFileProblem(err.reason), retryable: false }
    }
    if (err.status === 413) return { message: TOO_LARGE, retryable: false }
    if (err.status === 415) return { message: NOT_A_BACKUP, retryable: false }
    if (err.status === 429) {
      return err.code === 'restore_in_progress'
        ? {
            message:
              'A restore is already running for your notebook. Wait for it to finish.',
            retryable: true,
          }
        : {
            message: 'Too many restores. Wait a few minutes and try again.',
            retryable: true,
          }
    }
  }
  // 503, a broken connection or an unexpected status. Restoring the same file
  // again is always safe: pages already in the notebook are skipped.
  return { message: RESTORE_FAILED, retryable: true }
}

// The notebook kept its own type for an exercise the file classified the
// other way, so the kept type is the opposite of the file's.
function keptType(backup: BackupFile, name: string): string | null {
  const exercise = backup.exercises.find((e) => e.name === name)
  if (exercise === undefined) return null
  return exercise.isBodyweight ? 'Loaded' : 'Bodyweight'
}

function RestoreSection({
  unmounted,
  onSessionGone,
}: {
  unmounted: RefObject<boolean>
  onSessionGone: () => void
}) {
  const [state, setState] = useState<RestoreState>({ step: 'idle' })
  const focusRef = useRef<HTMLDivElement>(null)

  // Land keyboard and screen-reader users on what changed: the summary when
  // it appears, the result when the restore ends.
  useEffect(() => {
    if (state.step === 'summary' || state.step === 'done') {
      focusRef.current?.focus()
    }
  }, [state.step])

  async function choose(file: File) {
    setState({ step: 'reading', name: file.name })
    const checked = await precheckBackupFile(file)
    if (unmounted.current) return
    if (!checked.ok) {
      setState({
        step: 'failed',
        name: file.name,
        message: describeFileProblem(checked.reason),
        retry: null,
      })
      return
    }
    setState({
      step: 'summary',
      name: file.name,
      file,
      backup: checked.file,
      summary: checked.summary,
    })
  }

  async function restore(name: string, file: File, backup: BackupFile) {
    setState({ step: 'restoring', name, file, backup })
    try {
      // The original bytes, not the parsed copy: the server validates exactly
      // what was saved.
      const result = await restoreBackup(file)
      if (unmounted.current) return
      setState({ step: 'done', name, backup, result })
    } catch (err) {
      if (unmounted.current) return
      const problem = describeRestoreError(err)
      if (problem === null) {
        onSessionGone()
        return
      }
      setState({
        step: 'failed',
        name,
        message: problem.message,
        retry: problem.retryable ? { file, backup } : null,
      })
    }
  }

  // The native file input, visually hidden but focusable, with its label drawn
  // as the button. Clearing the value lets the same file be chosen again.
  function filePicker(label: string, className: string) {
    return (
      <div className="backup-file">
        <input
          id="backup-file-input"
          className="visually-hidden backup-file-input"
          type="file"
          accept=".json,application/json"
          onChange={(e) => {
            const file = e.target.files?.[0]
            e.target.value = ''
            if (file !== undefined) void choose(file)
          }}
        />
        <label htmlFor="backup-file-input" className={className}>
          {label}
        </label>
      </div>
    )
  }

  const chooseAnother = filePicker('Choose another file', 'btn btn-ghost')

  return (
    <section className="privacy-block" aria-labelledby="backup-restore">
      <h2 id="backup-restore">Restore from a backup</h2>

      {state.step === 'idle' && (
        <>
          <p>
            Adds the pages from a backup that aren&apos;t in your notebook.
            Nothing already here is changed or removed.
          </p>
          {filePicker('Choose backup file', 'btn btn-secondary btn-block')}
        </>
      )}

      {state.step === 'reading' && (
        <p className="backup-line" role="status">
          Reading {state.name}…
        </p>
      )}

      {state.step === 'summary' && (
        <>
          <div
            ref={focusRef}
            className="backup-focus"
            tabIndex={-1}
            aria-label="Backup file summary"
          >
            <ul className="backup-summary">
              <li>
                <span className="backup-file-name">{state.name}</span>
              </li>
              <li>
                <span>Backup taken</span>
                <span className="num">
                  {formatMoment(state.summary.takenAt)}
                </span>
              </li>
              <li>
                <span>Pages</span>
                <span className="num">
                  <strong>{formatNumber(state.summary.pages)}</strong>
                  {state.summary.firstDate !== null &&
                    state.summary.lastDate !== null &&
                    `, ${formatWorkoutLongDate(state.summary.firstDate)} – ${formatWorkoutLongDate(state.summary.lastDate)}`}
                </span>
              </li>
              <li>
                <span>Sets · exercises</span>
                <span className="num">
                  {formatNumber(state.summary.sets)} ·{' '}
                  {formatNumber(state.summary.exercises)}
                </span>
              </li>
            </ul>
          </div>
          <p className="muted">Pages already in your notebook are skipped.</p>
          <div className="backup-actions">
            <button
              className="btn btn-primary btn-block"
              type="button"
              onClick={() => void restore(state.name, state.file, state.backup)}
            >
              Restore missing pages
            </button>
            {chooseAnother}
          </div>
        </>
      )}

      {state.step === 'restoring' && (
        <div className="backup-status" role="status" aria-live="polite">
          <p className="backup-line">Restoring… Keep this page open.</p>
          <progress className="backup-progress" aria-label="Restore progress" />
        </div>
      )}

      {state.step === 'done' && (
        <RestoreDone
          result={state.result}
          backup={state.backup}
          focusRef={focusRef}
          onAgain={() => setState({ step: 'idle' })}
        />
      )}

      {state.step === 'failed' && (
        <>
          <ul className="backup-summary">
            <li>
              <span className="backup-file-name">{state.name}</span>
            </li>
          </ul>
          <p className="form-message" role="alert">
            {state.message}
          </p>
          <div className="backup-actions">
            {state.retry !== null && (
              <button
                className="btn btn-secondary btn-block"
                type="button"
                onClick={() => {
                  const { retry } = state
                  if (retry !== null)
                    void restore(state.name, retry.file, retry.backup)
                }}
              >
                Try again
              </button>
            )}
            {chooseAnother}
          </div>
        </>
      )}
    </section>
  )
}

function RestoreDone({
  result,
  backup,
  focusRef,
  onAgain,
}: {
  result: RestoreResult
  backup: BackupFile
  focusRef: RefObject<HTMLDivElement | null>
  onAgain: () => void
}) {
  const again = (
    <button className="btn btn-ghost" type="button" onClick={onAgain}>
      Restore another file
    </button>
  )

  if (result.pagesAdded === 0) {
    return (
      <>
        <div
          ref={focusRef}
          className="backup-focus"
          tabIndex={-1}
          role="status"
          aria-live="polite"
        >
          <p className="backup-result">Nothing to restore.</p>
          <p className="muted">
            {result.pagesAlreadyPresent === 0
              ? 'The backup has no pages.'
              : result.pagesAlreadyPresent === 1
                ? 'Its one page is already in your notebook.'
                : `All ${formatNumber(result.pagesAlreadyPresent)} pages are already in your notebook.`}
          </p>
        </div>
        <div className="backup-actions">{again}</div>
      </>
    )
  }

  const present = result.pagesAlreadyPresent
  return (
    <>
      <div
        ref={focusRef}
        className="backup-focus"
        tabIndex={-1}
        role="status"
        aria-live="polite"
      >
        <p className="backup-result">
          Restored <strong>{formatCount(result.pagesAdded, 'page')}</strong> and{' '}
          <strong>{formatCount(result.setsAdded, 'set')}</strong>.
        </p>
        <ul className="backup-result-lines">
          {present > 0 && (
            <li>
              {present === 1
                ? '1 page was already in your notebook.'
                : `${formatNumber(present)} pages were already in your notebook.`}
            </li>
          )}
          {result.exercisesCreated.length > 0 && (
            <li>
              {result.exercisesCreated.length === 1
                ? 'New exercise: '
                : 'New exercises: '}
              {result.exercisesCreated.join(', ')}.
            </li>
          )}
          {result.classificationKept.map((name) => {
            const type = keptType(backup, name)
            return (
              <li key={name}>
                {name} kept its current type
                {type === null ? '' : ` (${type})`}.
              </li>
            )
          })}
          {result.optionalDetailsDropped > 0 && (
            <li>
              Titles, gyms, notes and bodyweight weren&apos;t restored, because
              optional workout details aren&apos;t allowed.{' '}
              <Link to="/account/privacy/optional-details">
                Optional workout details
              </Link>
            </li>
          )}
        </ul>
      </div>
      <div className="backup-actions">
        <Link to="/workouts" className="btn btn-secondary btn-block">
          Open sessions
        </Link>
        {again}
      </div>
    </>
  )
}
