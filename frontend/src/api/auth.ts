import { request } from './client'

// Wire types for /auth/*, one per record in backend/GymNotebook.Api. Property
// names are camelCase because System.Text.Json writes `AuthResponse(string Token)`
// as `{ "token": ... }` and reads `{ "displayName": ... }` into `DisplayName`.
// Keeping them next to the calls that use them, rather than in one big types file,
// means a change to a backend record has exactly one frontend file to update.

// The address is sent as typed; the backend trims and lowercases it (specs/002
// FR-001), so the screen doesn't have to and can't disagree with it.
interface LoginRequest {
  email: string
  password: string
}

// turnstileToken is what the Cloudflare widget handed the screen (screens/
// Turnstile.tsx). Optional: left out when the check is off (no site key), and
// the API only requires it when its own secret is set. A missing or refused
// token is 400 { code: "captcha" }.
interface RegisterRequest {
  email: string
  password: string
  displayName: string
  turnstileToken?: string
}

// "Send the link again" (POST /auth/verification). The password is required so
// that the route can't be used to make the app email any address on demand.
interface ResendVerificationRequest {
  email: string
  password: string
}

// "Forgot your password?" (POST /auth/password-reset). The address, and the
// Turnstile token as on register.
interface PasswordResetRequest {
  email: string
  turnstileToken?: string
}

// The reset link's token, read from the URL fragment, and the password to set.
interface ConfirmPasswordResetRequest {
  token: string
  newPassword: string
}

export interface AuthResponse {
  token: string
}

export interface VerifyEmailResponse {
  email: string
}

export interface MeResponse {
  userId: number
  displayName: string
  email: string
}

interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
}

// Login resolves to the token or throws: ApiError for 400/401/403/429 (mapped to
// prose by describeAuthError, except 403 email_not_verified, which the screen
// turns into "check your inbox"), fetch's own TypeError when the API is
// unreachable. Storing the token is the caller's job — these are transport only,
// so a test or a later "re-authenticate" flow can call them without side effects.

export function login(body: LoginRequest): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/login', { method: 'POST', body })
}

// 202 with no body for every valid request, whether or not the address already
// has an account (specs/002 FR-011): no token comes back, because the account
// can't sign in until the address is confirmed. The difference only reaches the
// inbox, so the screen shows "check your inbox" either way.
export function register(body: RegisterRequest): Promise<void> {
  return request<void>('/auth/register', { method: 'POST', body })
}

// Always 204: whether a link went out depends on the account, and the answer
// must not.
export function resendVerification(
  body: ResendVerificationRequest,
): Promise<void> {
  return request<void>('/auth/verification', { method: 'POST', body })
}

// The confirmation link's token, read from the URL fragment. Its failures are
// 400 with code "expired" or "invalid" — never 401, so they can't be mistaken
// for a session ending (FR-009). `local` all the same: a stale session in this
// browser has nothing to do with whether the link works.
export function verifyEmail(token: string): Promise<VerifyEmailResponse> {
  return request<VerifyEmailResponse>('/auth/verify-email', {
    method: 'POST',
    body: { token },
    unauthorized: 'local',
  })
}

// 202 for every well-formed address, whether or not it has an account (specs/002
// FR-013): the screen can only say "if there's an account, a link is on its way".
export function requestPasswordReset(
  body: PasswordResetRequest,
): Promise<void> {
  return request<void>('/auth/password-reset', { method: 'POST', body })
}

// Sets the new password and returns a fresh session token, which the caller
// stores: the reset signs this browser in and every other session out. Link
// failures are 400 expired/invalid, like verifyEmail, and `local` for the same
// reason — whatever session this browser held has nothing to do with the link.
export function confirmPasswordReset(
  body: ConfirmPasswordResetRequest,
): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/password-reset/confirm', {
    method: 'POST',
    body,
    unauthorized: 'local',
  })
}

// No options: GET, no body, so no Content-Type — the cheapest authenticated
// request there is, which is what the route guard wants since it runs on every
// navigation.
export function me(): Promise<MeResponse> {
  return request<MeResponse>('/auth/me')
}

// The backend bumps token_version and returns a fresh token; the caller must
// store it before navigating anywhere, or the guard's next /auth/me is a 401.
// Its 401 means "current password is wrong", so it stays with the screen
// rather than signing the user out (unauthorized: 'local').

export function changePassword(
  body: ChangePasswordRequest,
): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/change-password', {
    method: 'POST',
    body,
    unauthorized: 'local',
  })
}
