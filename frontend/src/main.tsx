import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './styles/tokens.css'
import './styles/base.css'
import { RouterProvider } from 'react-router'
import { router } from './routes.tsx'
import { abortPendingRequests } from './api/client'
import { revokePendingDownloads } from './api/download'
import { installInvalidation } from './auth/invalidation'
import { clearToken } from './auth/token'

// Session invalidation (auth/invalidation.ts), wired to the real browser and
// router before the first render, so even the first route guard's 401 goes
// through it.
installInvalidation({
  clearToken,
  abortPendingRequests,
  revokeDownloads: () => revokePendingDownloads(),
  showSignedOut: () => {
    // The route guard may already be redirecting there.
    if (router.state.location.pathname !== '/login') {
      void router.navigate('/login', { replace: true })
    }
  },
  reload: () => window.location.reload(),
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
)
