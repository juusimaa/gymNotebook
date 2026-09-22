// Draft rows in the session editor need a stable identity before the server has
// given them one: React keys, and the handles the update helpers in
// newWorkoutDraft.ts address a single exercise or set by. They live only while the
// page is being edited — never sent to the API, never stored — so all they have to
// be is unique within the open page.
//
// crypto.randomUUID() is declared [SecureContext] in the Web Crypto specification,
// which means the browser defines it on HTTPS origins and on localhost but nowhere
// else. Opening the app over plain HTTP at a LAN address — a phone on the same
// network as the dev machine — leaves it undefined, and calling it threw where the
// editor asked for its first id. Everything else on `crypto` stays available, so
// the guard is on this one method rather than on the object.
//
// The fallback is a counter rather than random bytes on purpose: uniqueness within
// the page is the whole requirement, and a counter satisfies it exactly, where
// randomness would only approximate it. The timestamp keeps ids from two mounts of
// the editor distinct in a React DevTools trace.
let fallbackCounter = 0

// `source` is a parameter so the tests can exercise both branches without
// reassigning the global `crypto`; application code always calls createClientId().
export function createClientId(source: Crypto = crypto): string {
  if (typeof source.randomUUID === 'function') {
    return source.randomUUID()
  }

  fallbackCounter += 1
  return `draft-${Date.now().toString(36)}-${fallbackCounter.toString(36)}`
}
