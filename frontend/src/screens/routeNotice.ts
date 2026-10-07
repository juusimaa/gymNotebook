import { useEffect, useState } from 'react'
import { useLocation, useNavigate } from 'react-router'

// A one-line confirmation carried from the screen that did something to the
// screen it lands on: "Page saved", "Merged into Bench Press", "Page torn
// out". Before this, a save, a merge or a delete just changed screens, and
// nothing said whether it had worked.
//
// It travels in router state, like the login entry (auth/loginEntry.ts), so
// it never reaches the URL. Router state is `any` and survives reloads, so it
// is read defensively, and the landing screen clears it once shown: a reload
// or a later Back shouldn't announce the same save again.
export interface RouteNoticeState {
  notice: string
}

export function routeNotice(notice: string): RouteNoticeState {
  return { notice }
}

export function readRouteNotice(state: unknown): string | null {
  if (
    typeof state === 'object' &&
    state !== null &&
    'notice' in state &&
    typeof state.notice === 'string' &&
    state.notice.trim() !== ''
  ) {
    return state.notice
  }
  return null
}

// The notice the current screen was opened with, if any. It is captured once,
// then dropped from the history entry so it shows only on arrival.
export function useRouteNotice(): string | null {
  const location = useLocation()
  const navigate = useNavigate()
  const [notice] = useState(() => readRouteNotice(location.state))

  useEffect(() => {
    if (notice !== null && readRouteNotice(location.state) !== null) {
      void navigate(`${location.pathname}${location.search}`, {
        replace: true,
        state: null,
      })
    }
  }, [notice, location.pathname, location.search, location.state, navigate])

  return notice
}
