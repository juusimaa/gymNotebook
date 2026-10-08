import { request, send } from './client'
import { readCompleteJson } from './download'

// Wire types and calls for Backup & restore (specs/004 contracts/api.md). All
// three routes are mapped whether or not the privacy feature is on (plan D2),
// so unlike api/privacy.ts a 404 here is an error, never "feature off".

export type BackupFormat = 'json' | 'csv'

export interface LastBackup {
  // A UTC instant as an ISO-8601 string; null before the first full backup.
  lastBackupAt: string | null
}

export function getLastBackup(): Promise<LastBackup> {
  return request<LastBackup>('/account/backup')
}

// The saved file's name, dated by the browser's calendar day, so several
// backups sit side by side in a downloads folder: gym-notebook-2026-10-08.json.
// The server's Content-Disposition can't be read cross-origin without extra
// CORS configuration, so the client names the file itself.
export function backupFileName(
  format: BackupFormat,
  now: Date = new Date(),
): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  const day = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
  return `gym-notebook-${day}.${format}`
}

// POST /account/export: the whole notebook as one JSON file (specs/001 user
// story 3). `format` doesn't change the response — the browser writes the CSV
// from the same JSON (plan D4) — only whether a completed download records
// the last full backup (json) or not (csv).
//
// Resolves only with a complete file (see readCompleteJson). Rejects with
// ApiError 400 "password_verification_failed" for a wrong password, 401 when the
// session no longer works, 429 for too many attempts or "export_in_progress",
// 503 "temporarily_unavailable"; with fetch's own error when the connection
// broke; and with an AbortError when `signal` aborts. `onReceiving` fires once
// the password was accepted and the file has started arriving. The password is
// sent as typed and kept nowhere, so a retry always asks for it again. The body
// is read inside send(), so a sign-out anywhere aborts the download too.
export function exportNotebook(
  currentPassword: string,
  format: BackupFormat,
  options: { signal?: AbortSignal; onReceiving?: () => void } = {},
): Promise<Blob> {
  return send(
    '/account/export',
    {
      method: 'POST',
      body: { currentPassword, format },
      signal: options.signal,
      // A POST that only reads: a 401 here can't have left a half-saved change.
      changesData: false,
    },
    (response) => {
      options.onReceiving?.()
      return readCompleteJson(response)
    },
  )
}

export interface RestoreResult {
  pagesAdded: number
  setsAdded: number
  pagesAlreadyPresent: number
  // Exercise names as written in the file.
  exercisesCreated: string[]
  // Exercises that already existed with the other type; the notebook's own
  // type was kept.
  classificationKept: string[]
  // Pages whose title, gym, notes and bodyweight were left out because the
  // account doesn't allow optional workout details.
  optionalDetailsDropped: number
}

// POST /account/restore: adds the pages from a backup file that the notebook
// doesn't have, in one transaction (all or nothing). The file is sent byte for
// byte, exactly as it was saved. No password (plan D8).
//
// Rejects with ApiError 400 "backup_invalid" with a `reason` (not_a_backup,
// unsupported_version, broken_reference, invalid_value), 413 for a file over
// the server's cap, 415 for a body that isn't JSON, 429 "restore_in_progress"
// or the per-account rate limit, 503 "temporarily_unavailable" (rolled back),
// 401 when the session no longer works, and fetch's own error when the
// connection broke — in which case the outcome is unknown to the client, but
// restoring the same file again is safe: pages already there are skipped.
export function restoreBackup(file: Blob): Promise<RestoreResult> {
  return request<RestoreResult>('/account/restore', {
    method: 'POST',
    jsonFile: file,
  })
}
