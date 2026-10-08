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

// The token's claims (its middle, base64url-encoded part), or null when it
// isn't a JWT this can read. Only for the browser's own timing and cross-tab
// decisions: nothing here is verified, and the server checks every token anyway.
function readClaims(token: string): Record<string, unknown> | null {
  const payload = token.split('.')[1]
  if (payload === undefined) {
    return null
  }
  try {
    // base64url → base64, which is what atob reads.
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
    const claims: unknown = JSON.parse(json)
    if (typeof claims === 'object' && claims !== null) {
      return claims as Record<string, unknown>
    }
  } catch {
    // Not a JWT we can read.
  }
  return null
}

// A numeric claim, such as `exp` or `iat` (seconds since the epoch, RFC 7519),
// in milliseconds; undefined when missing or not a number.
function readTime(
  claims: Record<string, unknown> | null,
  name: string,
): number | undefined {
  const value = claims?.[name]
  return typeof value === 'number' ? value * 1000 : undefined
}

// Whose token this is: the `sub` claim, the user id. auth/invalidation.ts uses
// it to tell a renewed token (same user, nothing to do) from another sign-in.
export function getTokenSubject(token: string): string | null {
  const sub = readClaims(token)?.sub
  return typeof sub === 'string' ? sub : null
}

// Whether to renew before the next request (specs/003 D7): the token is past
// half its lifetime (`iat` to `exp`) and not yet expired. Half-life means a
// 30-minute token renews after 15 minutes of use, and a phone left on the bench
// for up to 15 more still comes back with one that works. An expired token isn't
// renewable; its request meets the 401 as before. A token without `iat` (issued
// before renewal existed) never renews and simply runs out.
//
// The device's clock is compared with the server's times. A clock running far
// ahead only skips renewal, and one far behind renews early; neither breaks a
// request, and the server enforces the real expiry and the 12-hour cap.
export function shouldRenew(token: string, now: Date = new Date()): boolean {
  const claims = readClaims(token)
  const issuedAt = readTime(claims, 'iat')
  const expiresAt = readTime(claims, 'exp')
  if (issuedAt === undefined || expiresAt === undefined) {
    return false
  }
  const halfLife = issuedAt + (expiresAt - issuedAt) / 2
  return now.getTime() >= halfLife && now.getTime() < expiresAt
}

// Whether the token's own `exp` claim has passed. The server has already said
// the token is no good (a 401); this only reads *why*, to tell a plain expiry
// apart from a password change elsewhere or a deleted account. An expiry keeps
// the editor draft for the same user's next sign-in (screens/editorDraftStorage.ts).
// The server tolerates some clock skew past `exp`, so by the time it refuses
// an expired token this check agrees. Anything unreadable answers false, the
// stricter path: the draft is cleared.
export function isTokenExpired(token: string, now: Date = new Date()): boolean {
  const expiresAt = readTime(readClaims(token), 'exp')
  return expiresAt !== undefined && expiresAt <= now.getTime()
}
