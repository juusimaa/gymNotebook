import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './styles/tokens.css'
import './styles/base.css'
import { RouterProvider } from 'react-router'
import { router } from './routes.tsx'
import { abortPendingRequests, installRenewalOnVisible } from './api/client'
import { revokePendingDownloads } from './api/download'
import { installInvalidation } from './auth/invalidation'
import { clearToken, getToken } from './auth/token'
import {
  clearEditorDrafts,
  holdEditorDraftsForSignIn,
} from './screens/editorDraftStorage'

// No token at startup: whatever session this tab had is over, so its editor
// drafts go too, except one this tab held for sign-in after its token expired
// (screens/editorDraftStorage.ts). This catches a sign-out in another tab that
// this tab missed because it was discarded at the time.
if (getToken() === null) {
  clearEditorDrafts({ keepHeld: true })
}

// Session invalidation (auth/invalidation.ts), wired to the real browser and
// router before the first render, so even the first route guard's 401 goes
// through it.
installInvalidation({
  clearToken,
  abortPendingRequests,
  revokeDownloads: () => revokePendingDownloads(),
  clearDrafts: () => clearEditorDrafts(),
  holdDrafts: () => holdEditorDraftsForSignIn(),
  showSignedOut: () => {
    // The route guard may already be redirecting there.
    if (router.state.location.pathname !== '/login') {
      void router.navigate('/login', { replace: true })
    }
  },
  reload: () => window.location.reload(),
})

// Renews the session token when the tab comes back into view (api/client.ts).
installRenewalOnVisible()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
)
