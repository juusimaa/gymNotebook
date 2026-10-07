// Cloudflare Turnstile's public site key (Turnstile.tsx). Empty switches the bot
// check off on every form: the widget isn't shown and no token is sent, which is
// right only when the API has no TURNSTILE_SECRET_KEY either — the two are
// switched on together (specs/002 plan, Configuration).
//
// Two sources, like the API URL in api/client.ts: the deployed container's
// config.js (window.__TURNSTILE_SITE_KEY__, rendered from the TURNSTILE_SITE_KEY
// env var by runtime-config.sh), else Vite's VITE_TURNSTILE_SITE_KEY for the dev
// server. `||` rather than `??` because the container writes an empty string
// when the variable is unset, and empty should fall through, not win.
//
// The site key is public by design — it's in every page that shows the widget —
// so unlike the secret it can live in the bundle and in config.js.
export function resolveTurnstileSiteKey(
  runtime: string | undefined,
  buildTime: string | undefined,
): string {
  return (runtime || buildTime || '').trim()
}

export const TURNSTILE_SITE_KEY = resolveTurnstileSiteKey(
  typeof window === 'undefined' ? undefined : window.__TURNSTILE_SITE_KEY__,
  import.meta.env.VITE_TURNSTILE_SITE_KEY,
)
