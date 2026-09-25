import { createBrowserRouter, Navigate, Outlet } from 'react-router'
import { requireAuth } from './auth/requireAuth.ts'
import { requireNoticeAcknowledged } from './auth/requireNoticeAcknowledged.ts'
import AccountDeleted from './screens/AccountDeleted.tsx'
import AccountPrivacy from './screens/AccountPrivacy.tsx'
import ChangePassword from './screens/ChangePassword.tsx'
import Cover from './screens/Cover.tsx'
import DeleteAccount from './screens/DeleteAccount.tsx'
import ExportData from './screens/ExportData.tsx'
import Login from './screens/Login.tsx'
import NoticeGate from './screens/NoticeGate.tsx'
import PrivacyNotice from './screens/PrivacyNotice.tsx'
import Sessions from './screens/Sessions.tsx'
import NewWorkout from './screens/NewWorkout.tsx'
import WorkoutDetail from './screens/WorkoutDetail.tsx'
import Progress from './screens/Progress.tsx'
import Exercises from './screens/Exercises.tsx'
import EditExercise from './screens/EditExercise.tsx'

// The route table, one entry per screen in docs/ui/README.md. Data-mode router
// (createBrowserRouter) rather than <BrowserRouter><Routes>, for the loader below.
//
// /login, /privacy (the public notice) and /account/deleted (the completion
// screen after a deletion, when there's no account left to be signed in to)
// are the only screens outside the guard. Everything else sits under one
// pathless layout route: no path of its own, a loader (requireAuth) that runs
// before render and on every navigation beneath it, and a bare <Outlet /> that
// renders whichever child matched. Screens read the signed-in user with
// useRouteLoaderData('auth') — that id is the handle. The catch-all last sends
// unknown URLs to "/", which then runs the guard, so a signed-out /typo ends at
// /login; `replace` keeps the bad URL out of history.
//
// Inside the guard, the notebook screens sit one level deeper, under a second
// pathless layout whose loader is the privacy notice gate (specs/001
// contracts/ui.md): it runs before any notebook screen renders — and so before
// any notebook fetch — on "Open the notebook" and on every deep link. The
// cover, change-password and privacy screens (export and deletion included)
// stay outside it, reachable without acknowledging the notice.
export const router = createBrowserRouter([
  { path: '/login', element: <Login /> },
  { path: '/privacy', element: <PrivacyNotice /> },
  { path: '/account/deleted', element: <AccountDeleted /> },
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
      { path: '/account/privacy', element: <AccountPrivacy /> },
      { path: '/account/privacy/notice', element: <NoticeGate /> },
      { path: '/account/export', element: <ExportData /> },
      { path: '/account/delete', element: <DeleteAccount /> },
      {
        id: 'notebook',
        loader: requireNoticeAcknowledged,
        element: <Outlet />,
        children: [
          { path: '/workouts', element: <Sessions /> },
          { path: '/workouts/new', element: <NewWorkout /> },
          { path: '/workouts/:workoutId/edit', element: <NewWorkout /> },
          { path: '/workouts/:workoutId', element: <WorkoutDetail /> },
          { path: '/progress', element: <Progress /> },
          { path: '/exercises', element: <Exercises /> },
          { path: '/exercises/:exerciseId', element: <EditExercise /> },
        ],
      },
    ],
  },
  { path: '*', element: <Navigate to="/" replace /> },
])
