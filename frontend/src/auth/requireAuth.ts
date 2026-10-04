import { redirect } from 'react-router'
import { ApiError } from '../api/client'
import { me, type MeResponse } from '../api/auth'
import { adoptEditorDrafts } from '../screens/editorDraftStorage'
import { clearToken, getToken } from './token'

// Route loader for the layout route every signed-in screen sits under. A loader
// runs before the route renders — so a signed-out visit never flashes the cover —
// and again on every navigation beneath it, so a token that expires mid-session is
// caught at the next screen change, not the next reload. GET /auth/me is where the
// backend checks both expiry and token_version, so the rest of the app can assume
// that if it rendered under this route, the token was good a moment ago. (A 401
// anywhere else ends the session through auth/invalidation.ts.) Child screens read the user with
// useRouteLoaderData('auth') instead of fetching it again.
export async function requireAuth(): Promise<MeResponse> {
  if (getToken() === null) {
    throw redirect('/login')
  }

  try {
    const user = await me()
    // An editor draft held through an expired token is this user's again, and
    // one left by another account in this tab is removed before any screen
    // could show it.
    adoptEditorDrafts(user.userId)
    return user
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) {
      clearToken()
      throw redirect('/login')
    }
    throw err
  }
}
