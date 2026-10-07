import { useEffect, useRef, useState } from 'react'
import { TURNSTILE_SITE_KEY } from './turnstileSiteKey'

// Cloudflare Turnstile, the bot check on "Create account" and "Forgot your
// password?": the two forms that make the API email an address nobody has
// proven yet (backend Turnstile.cs, specs/002 contracts/ui.md → Turnstile
// widget). The widget runs Cloudflare's challenge in an iframe and hands back a
// token; the form sends it, and the API asks Cloudflare whether it's good.
//
// Switched on by a site key (turnstileSiteKey.ts). Without one this renders
// nothing and loads nothing, so local development never contacts Cloudflare.
//
// A token is good for one check. The form remounts this (a new `key`) after
// every attempt, so a refused submit gets a fresh one.

const SCRIPT_URL =
  'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'

// Below this the "flexible" widget (100% wide, at least 300px) would overflow:
// a 320px phone leaves about 265px between the page's gutters. There the
// compact widget (150 x 140) is used instead (spec FR-025: it must fit).
const FLEXIBLE_MIN_WIDTH = 300

// One script load for the page, however many times a form mounts the widget.
// Loaded on first use rather than from index.html, so Cloudflare sees only the
// visitors who open one of these two forms (docs/privacy/suppliers.md).
let scriptLoad: Promise<TurnstileApi> | null = null

function loadScript(): Promise<TurnstileApi> {
  if (window.turnstile) return Promise.resolve(window.turnstile)
  scriptLoad ??= new Promise<TurnstileApi>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = SCRIPT_URL
    script.async = true
    script.onload = () => {
      if (window.turnstile) resolve(window.turnstile)
      else reject(new Error('Turnstile loaded without its API'))
    }
    script.onerror = () => {
      // Forget the failed load, so the next mount (the next attempt) retries.
      scriptLoad = null
      script.remove()
      reject(new Error('Turnstile failed to load'))
    }
    document.head.appendChild(script)
  })
  return scriptLoad
}

interface TurnstileProps {
  // Names the form; the API refuses a token solved for the other one.
  action: 'signup' | 'password_reset'
  // Gets the token once the check passes, and null when it expires or fails,
  // so the form can hold its submit until there is one.
  onToken: (token: string | null) => void
}

export default function Turnstile({ action, onToken }: TurnstileProps) {
  const box = useRef<HTMLDivElement>(null)
  const [loadFailed, setLoadFailed] = useState(false)

  // The latest callback, without re-rendering the widget when it changes: the
  // widget is created once per mount and calls whatever this points at.
  const callback = useRef(onToken)
  useEffect(() => {
    callback.current = onToken
  }, [onToken])

  useEffect(() => {
    if (TURNSTILE_SITE_KEY === '') return
    let widgetId: string | null = null
    let cancelled = false

    loadScript().then(
      (turnstile) => {
        if (cancelled || box.current === null) return
        widgetId = turnstile.render(box.current, {
          sitekey: TURNSTILE_SITE_KEY,
          action,
          // Follows the OS colour scheme, as the app's own dark theme does.
          theme: 'auto',
          size:
            box.current.clientWidth < FLEXIBLE_MIN_WIDTH
              ? 'compact'
              : 'flexible',
          callback: (token) => callback.current(token),
          'expired-callback': () => callback.current(null),
          'error-callback': () => callback.current(null),
        })
      },
      () => {
        if (cancelled) return
        setLoadFailed(true)
        callback.current(null)
      },
    )

    // Unmounting (a new attempt, another mode, leaving the screen) removes the
    // widget; StrictMode's mount-unmount-mount in development goes through here
    // too, which is why the render waits for `cancelled`.
    return () => {
      cancelled = true
      if (widgetId !== null) window.turnstile?.remove(widgetId)
    }
  }, [action])

  if (TURNSTILE_SITE_KEY === '') return null
  return (
    <>
      <div ref={box} className="login-turnstile" />
      {/* Without the script there's no token, and the submit button stays
          disabled; say why rather than leaving a dead button. */}
      {loadFailed && (
        <p className="form-message" role="alert">
          The bot check couldn&apos;t load. Check your connection and try again.
        </p>
      )}
    </>
  )
}
