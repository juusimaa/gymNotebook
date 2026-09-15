import { createBrowserRouter } from 'react-router'
import App from './App.tsx'
import Login from './screens/Login.tsx'

// The route table, one entry per screen in docs/ui/README.md. Data-mode router
// (createBrowserRouter) rather than <BrowserRouter><Routes>, so PR 4's auth guard
// can be a layout route with the protected screens as its children.
export const router = createBrowserRouter([
  {
    path: '/',
    // Placeholder until PR 4 makes this the cover.
    element: <App />,
  },
  {
    path: '/login',
    element: <Login />,
  },
])
