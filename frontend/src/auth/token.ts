// The one place that knows where the JWT lives. The API client, the login screen,
// sign-out and the route guard all go through these three functions, so moving
// the token to sessionStorage or memory later is a change here and nowhere else.
// localStorage can throw when a browser blocks site data; for a single-user app
// that's a fair "this browser can't run the app" failure, so it isn't caught.
//
// The key is exported for auth/invalidation.ts: another tab removing this key is how this
// tab learns the session ended there.
export const TOKEN_KEY = 'gymnotebook.token'

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string): void {
  localStorage.setItem(TOKEN_KEY, token)
}

export function clearToken(): void {
  localStorage.removeItem(TOKEN_KEY)
}

// Whether the token's own `exp` claim has passed. The server has already said
// the token is no good (a 401); this only reads *why*, to tell a plain expiry
// apart from a password change elsewhere or a deleted account. An expiry keeps
// the editor draft for the same user's next sign-in (screens/editorDraftStorage.ts).
// The server tolerates some clock skew past `exp`, so by the time it refuses
// an expired token this check agrees. Anything unreadable answers false, the
// stricter path: the draft is cleared.
export function isTokenExpired(token: string, now: Date = new Date()): boolean {
  const payload = token.split('.')[1]
  if (payload === undefined) {
    return false
  }
  try {
    // base64url → base64, which is what atob reads.
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
    const claims: unknown = JSON.parse(json)
    if (
      typeof claims === 'object' &&
      claims !== null &&
      'exp' in claims &&
      typeof claims.exp === 'number'
    ) {
      // `exp` is seconds since the epoch (RFC 7519).
      return claims.exp * 1000 <= now.getTime()
    }
  } catch {
    // Not a JWT we can read: fall through to "not expired".
  }
  return false
}
