import { createBrowserRouter, Navigate, Outlet } from 'react-router'
import { requireAuth } from './auth/requireAuth.ts'
import ChangePassword from './screens/ChangePassword.tsx'
import Cover from './screens/Cover.tsx'
import Login from './screens/Login.tsx'
import Sessions from './screens/Sessions.tsx'

// The route table, one entry per screen in docs/ui/README.md. Data-mode router
// (createBrowserRouter) rather than <BrowserRouter><Routes>, for the loader below.
//
// /login is the only screen outside the guard. Everything else sits under one
// pathless layout route: no path of its own, a loader (requireAuth) that runs
// before render and on every navigation beneath it, and a bare <Outlet /> that
// renders whichever child matched. Screens read the signed-in user with
// useRouteLoaderData('auth') — that id is the handle. The catch-all last sends
// unknown URLs to "/", which then runs the guard, so a signed-out /typo ends at
// /login; `replace` keeps the bad URL out of history.
export const router = createBrowserRouter([
  { path: '/login', element: <Login /> },
  {
    id: 'auth',
    loader: requireAuth,
    element: <Outlet />,
    // Shown on the initial load while the loader is still in flight: the bare
    // paper ground for ~50 ms. Without it the router logs a dev warning.
    HydrateFallback: () => null,
    children: [
      // An index route renders at the parent's own URL — "/" here.
      { index: true, element: <Cover /> },
      { path: '/change-password', element: <ChangePassword /> },
      { path: '/workouts', element: <Sessions /> },
    ],
  },
  { path: '*', element: <Navigate to="/" replace /> },
])
