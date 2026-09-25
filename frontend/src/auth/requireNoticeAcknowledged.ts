import { redirect, type LoaderFunctionArgs } from 'react-router'
import { ApiError } from '../api/client'
import { getAccountPrivacy } from '../api/privacy'
import { needsNoticeGate, noticeGateUrl } from './noticeGate'
import { clearToken, getToken } from './token'

// Route loader for the layout route every notebook screen sits under
// (routes.tsx). A loader runs before its route renders, and the screens fetch
// their data only once rendered, so a redirect from here means no notebook
// request is ever sent before the notice is acknowledged (contracts/ui.md →
// Notice transitions). It runs whenever the notebook is entered — from the
// cover's "Open the notebook" or any deep link — because entering matches this
// route afresh.
//
// It runs in parallel with requireAuth, not after it, so it handles a missing
// or rejected token the same way. Any other failure throws to the router's
// error screen: failing closed shows no notebook, never an unchecked one.
export async function requireNoticeAcknowledged({
  request,
}: LoaderFunctionArgs): Promise<null> {
  if (getToken() === null) {
    throw redirect('/login')
  }

  let state
  try {
    state = await getAccountPrivacy()
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) {
      clearToken()
      throw redirect('/login')
    }
    throw err
  }

  if (needsNoticeGate(state)) {
    // Path and query only; the gate validates it again before navigating back.
    const url = new URL(request.url)
    throw redirect(noticeGateUrl(url.pathname + url.search))
  }
  return null
}
