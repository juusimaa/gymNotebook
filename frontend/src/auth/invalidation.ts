import { onSessionEnded, type SessionEnded } from '../api/client'
import { TOKEN_KEY } from './token'

// What happens when this browser learns its session is over (specs/001
// contracts/ui.md → Invalidation and accessibility): the account was deleted,
// the password changed elsewhere, or the token expired. Three ways to learn it:
//
//   - A request answers 401. api/client.ts reports it here before the error
//     reaches the screen.
//   - Another tab signed out or deleted the account. It removed the token from
//     localStorage, and the browser fires a `storage` event in every other tab
//     of this origin: the existing key doubles as the cross-tab signal.
//   - The page came back from the back/forward cache, showing whatever it
//     showed when it was left. It is reloaded, so the route guards run again
//     before anything personal is on screen.
//
// Ending the session clears everything the app holds: the token, requests
// still in flight, export files waiting in object URLs, and — by navigating to
// /login, which unmounts every notebook screen — the notebook and editor state
// held in React. Files already downloaded stay on the device; nothing here can
// reach them.
//
// The browser pieces are passed in (InvalidationEnvironment), so the tests run
// in plain Node.

export interface InvalidationEnvironment {
  clearToken(): void
  abortPendingRequests(): void
  revokeDownloads(): void
  // Shows the signed-out UI: navigates to /login.
  showSignedOut(): void
  // Reloads the page, so the route guards revalidate before anything renders.
  reload(): void
}

// The subset of a StorageEvent this needs.
export interface TokenStorageChange {
  key: string | null
  newValue: string | null
}

// Set when the session ended on a write (research R4, Q5): the change may have
// been saved before the 401, so the sign-in screen warns before it's repeated.
// Module state, not storage: the warning belongs to this tab's navigation to
// /login, and a reload has nothing left to warn about.
let interruptedWrite = false

export function hasInterruptedWrite(): boolean {
  return interruptedWrite
}

// Called once the user has signed in again and seen the warning.
export function clearInterruptedWrite(): void {
  interruptedWrite = false
}

// Ends the session in this tab. `showSignedOut: false` is for a screen that
// explains the ending itself (account deletion) before offering sign-in.
export function endSession(
  env: InvalidationEnvironment,
  options: { changesData?: boolean; showSignedOut?: boolean } = {},
): void {
  if (options.changesData) {
    interruptedWrite = true
  }
  env.clearToken()
  env.abortPendingRequests()
  env.revokeDownloads()
  if (options.showSignedOut ?? true) {
    env.showSignedOut()
  }
}

// Another tab changed the token. Removed: that tab signed out or the account is
// gone, so this one ends its session too (clearing an already-cleared token is
// harmless). Replaced: someone signed in, possibly as a different account, so
// what this tab shows may not be theirs — reload and let the guards decide.
export function handleTokenStorageChange(
  change: TokenStorageChange,
  env: InvalidationEnvironment,
): void {
  if (change.key !== TOKEN_KEY && change.key !== null) {
    return
  }
  // key null: the whole of localStorage was cleared.
  if (change.newValue === null) {
    endSession(env)
  } else {
    env.reload()
  }
}

// `persisted` is true only for a back/forward-cache restore; a normal load
// already ran the guards.
export function handlePageShow(
  event: { persisted: boolean },
  env: InvalidationEnvironment,
): void {
  if (event.persisted) {
    env.reload()
  }
}

let installed: InvalidationEnvironment | null = null

// Wires everything up once, at startup (main.tsx). Returns an uninstall
// function, for tests.
export function installInvalidation(
  env: InvalidationEnvironment,
  target: Pick<Window, 'addEventListener' | 'removeEventListener'> = window,
): () => void {
  installed = env
  onSessionEnded((event: SessionEnded) =>
    endSession(env, { changesData: event.changesData }),
  )
  const onStorage = (event: StorageEvent) =>
    handleTokenStorageChange(event, env)
  const onPageShow = (event: PageTransitionEvent) => handlePageShow(event, env)
  target.addEventListener('storage', onStorage)
  target.addEventListener('pageshow', onPageShow)
  return () => {
    installed = null
    onSessionEnded(null)
    target.removeEventListener('storage', onStorage)
    target.removeEventListener('pageshow', onPageShow)
  }
}

// endSession with the installed environment and no navigation, for a screen
// that has its own message to show (the deletion screen).
export function endSessionHere(): void {
  if (installed === null) {
    throw new Error('installInvalidation() has not run.')
  }
  endSession(installed, { showSignedOut: false })
}
