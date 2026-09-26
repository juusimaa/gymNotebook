import { ApiError } from '../api/client'

// What a failed POST /account/delete means for the user (specs/001
// contracts/ui.md → Deletion transitions). The distinction that matters is
// whether the account is known to be intact:
//
//   - "retry": the server said nothing was deleted (wrong password, too many
//     attempts, busy for a moment). The form stays, and trying again is safe.
//   - "unknown": nobody can say from here. The server couldn't confirm its
//     commit, the connection broke mid-request, or the session was already
//     gone. The screen explains and offers ways to check, never a claim either
//     way. A 401 lands here too: it proves only that this session no longer
//     works, not that the deletion happened (contracts/api.md).
export type DeletionFailure =
  | { kind: 'retry'; message: string }
  // signedOut: the failure itself ended the session (a 401), so the way to
  // check is signing in again rather than opening the notebook.
  | { kind: 'unknown'; heading: string; message: string; signedOut: boolean }

export function describeDeletionFailure(err: unknown): DeletionFailure {
  if (err instanceof ApiError) {
    switch (err.status) {
      case 400:
        return {
          kind: 'retry',
          message:
            err.code === 'password_verification_failed'
              ? 'Check your current password and try again. Nothing was deleted.'
              : 'Enter your current password.',
        }
      case 429:
        return {
          kind: 'retry',
          message:
            'Too many attempts. Wait a minute and try again. Nothing was deleted.',
        }
      case 404:
        // The route isn't mapped: the privacy feature is switched off.
        return {
          kind: 'retry',
          message:
            'Account deletion is not available right now. Nothing was deleted.',
        }
      case 401:
        return {
          kind: 'unknown',
          heading: 'Your session has ended',
          message:
            "This session no longer gives access to the account. That doesn't show whether the account was deleted. Sign in again to check, or ask the privacy contact.",
          signedOut: true,
        }
      case 503:
        if (err.code === 'temporarily_unavailable') {
          return {
            kind: 'retry',
            message:
              'Your account is busy for a moment, so nothing was deleted. Try again in a few seconds.',
          }
        }
        if (err.code === 'deletion_outcome_unknown') {
          return {
            kind: 'unknown',
            heading: "We couldn't confirm the deletion",
            message:
              "The deletion may or may not have finished. Don't rely on either: check whether you can still open your notebook, and ask the privacy contact if you need certainty.",
            signedOut: false,
          }
        }
        break
    }
  }
  // A broken connection or an unexpected status: the request may have reached
  // the server and completed, so this is not a known failure.
  return {
    kind: 'unknown',
    heading: "We couldn't confirm the deletion",
    message:
      'The connection or the server failed before an answer arrived, so the deletion may or may not have finished. Check whether you can still open your notebook, and ask the privacy contact if you need certainty.',
    signedOut: false,
  }
}
