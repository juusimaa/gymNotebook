// Whether sign-in offers "Try the demo" (specs/007, docs/ui/README.md → Demo
// mode). The API maps POST /auth/demo only when its DEMO_MODE_ENABLED is
// "true", but the sign-in screen can't ask without starting a demo, so the
// frontend has its own switch, set together with the API's like the Turnstile
// keys.
//
// Two sources, as in turnstileSiteKey.ts: the deployed container's config.js
// (window.__DEMO_MODE_ENABLED__, rendered by runtime-config.sh), else Vite's
// VITE_DEMO_MODE_ENABLED for the dev server. `||` because the container writes
// an empty string when the variable is unset. Only the exact value "true"
// switches it on, the same fail-closed rule as the API's flag.
export function resolveDemoModeEnabled(
  runtime: string | undefined,
  buildTime: string | undefined,
): boolean {
  return (runtime || buildTime) === 'true'
}

export const DEMO_MODE_ENABLED = resolveDemoModeEnabled(
  typeof window === 'undefined' ? undefined : window.__DEMO_MODE_ENABLED__,
  import.meta.env.VITE_DEMO_MODE_ENABLED,
)
