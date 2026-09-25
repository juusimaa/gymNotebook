// Turning an API response into a file the browser saves (specs/001 contracts/ui.md
// → Export transitions). Two rules:
//
// Only a complete response becomes a download. The server aborts the connection
// when an export is cut short (deletion, sign-out elsewhere, time limit), which
// makes reading the body reject. As a second line of defence, in case a proxy
// ever turns an abort into a clean end of stream, the body must also parse as
// JSON: a truncated export never does, because its closing brace is sent last.
//
// The file lives in a temporary object URL that is always revoked: shortly after
// the save starts, or at once when the screen goes away (revokePendingDownloads).

// Thrown when the body ended but isn't a whole JSON document.
export class IncompleteDownloadError extends Error {
  constructor() {
    super('The download ended before the file was complete.')
  }
}

// Reads the whole body and checks it is one complete JSON document. Rejects with
// the fetch's own error if the connection broke or the signal aborted, and with
// IncompleteDownloadError if it ended early without an error.
export async function readCompleteJson(response: Response): Promise<Blob> {
  const text = await response.text()
  try {
    JSON.parse(text)
  } catch {
    throw new IncompleteDownloadError()
  }
  return new Blob([text], { type: 'application/json' })
}

// The browser APIs the save needs, injectable so the tests run in plain Node.
export interface DownloadEnvironment {
  createObjectURL(blob: Blob): string
  revokeObjectURL(url: string): void
  // Starts the browser's save for `url` under `fileName`.
  startSave(url: string, fileName: string): void
  schedule(callback: () => void, delayMs: number): void
}

// How long the object URL outlives the click. Revoking in the same tick can
// cancel the save in some browsers; a few seconds is plenty for it to start.
export const REVOKE_DELAY_MS = 10_000

const browserEnvironment: DownloadEnvironment = {
  createObjectURL: (blob) => URL.createObjectURL(blob),
  revokeObjectURL: (url) => URL.revokeObjectURL(url),
  startSave: (url, fileName) => {
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    link.click()
  },
  schedule: (callback, delayMs) => {
    setTimeout(callback, delayMs)
  },
}

// Object URLs created and not yet revoked, so a screen that goes away (or a
// sign-out) can release them immediately rather than wait for the timer.
const pending = new Set<string>()

export function saveBlob(
  blob: Blob,
  fileName: string,
  env: DownloadEnvironment = browserEnvironment,
): void {
  const url = env.createObjectURL(blob)
  pending.add(url)
  try {
    env.startSave(url, fileName)
  } finally {
    env.schedule(() => revoke(url, env), REVOKE_DELAY_MS)
  }
}

export function revokePendingDownloads(
  env: DownloadEnvironment = browserEnvironment,
): void {
  for (const url of [...pending]) {
    revoke(url, env)
  }
}

function revoke(url: string, env: DownloadEnvironment): void {
  // Deleting first makes a second revoke (timer after an early revoke) a no-op.
  if (pending.delete(url)) {
    env.revokeObjectURL(url)
  }
}
