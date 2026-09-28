import { useEffect, useState } from 'react'
import { getAccountPrivacy } from '../api/privacy'
import { optionalDetailsAllowed } from '../auth/noticeGate'

// Whether this account may see and enter a workout's title, location, notes
// and bodyweight (specs/001 user story 6). 'loading' until known; a failed
// check counts as 'not-allowed' — fail closed: hide the fields rather than
// offer ones the server may reject. `setStatus` lets the editor update it
// after an inline "Allow" or a rejected save, without refetching.
export type OptionalDetailsPermission = 'loading' | 'allowed' | 'not-allowed'

export function useOptionalDetailsAllowed() {
  const [status, setStatus] = useState<OptionalDetailsPermission>('loading')

  useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const state = await getAccountPrivacy()
        if (!cancelled) {
          setStatus(optionalDetailsAllowed(state) ? 'allowed' : 'not-allowed')
        }
      } catch {
        // A 401 has already signed the session out (api/client.ts); anything
        // else just keeps the fields hidden.
        if (!cancelled) setStatus('not-allowed')
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [])

  return { status, setStatus }
}
