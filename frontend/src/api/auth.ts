import { request } from './client'

// Wire types for /auth/*, one per record in backend/GymNotebook.Api. Property
// names are camelCase because System.Text.Json writes `AuthResponse(string Token)`
// as `{ "token": ... }` and reads `{ "inviteCode": ... }` into `InviteCode`.
// Keeping them next to the calls that use them, rather than in one big types file,
// means a change to a backend record has exactly one frontend file to update.

interface LoginRequest {
  username: string
  password: string
}

// `string | null` mirrors the backend's `string? InviteCode`. The screen sends
// whatever is in the field, empty string included — with INVITE_CODE unset the
// backend accepts anything, and with it set a blank is rejected the same 403 as a
// wrong one, so there's nothing for the client to special-case.
interface RegisterRequest {
  username: string
  password: string
  inviteCode: string | null
}

export interface AuthResponse {
  token: string
}

export interface MeResponse {
  userId: number
  username: string
}

interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
}

// Both resolve to the token or throw: ApiError for 400/401/403/409/429 (mapped to
// prose by describeAuthError), fetch's own TypeError when the API is unreachable.
// Storing the token is the caller's job — these are transport only, so a test or
// a later "re-authenticate" flow can call them without side effects.

export function login(body: LoginRequest): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/login', { method: 'POST', body })
}

export function register(body: RegisterRequest): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/register', { method: 'POST', body })
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
