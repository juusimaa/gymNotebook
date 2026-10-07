/// <reference types="vite/client" />

interface ViteTypeOptions {
  strictImportMetaEnv: unknown
}

interface ImportMetaEnv {
  readonly VITE_API_URL: string
  // Optional: unset switches the Turnstile check off (screens/turnstileSiteKey.ts).
  readonly VITE_TURNSTILE_SITE_KEY?: string
}

// The part of Cloudflare's Turnstile script API that screens/Turnstile.tsx uses
// (developers.cloudflare.com/turnstile/get-started/client-side-rendering/).
// Written out here rather than added as a types package: three members.
interface TurnstileRenderOptions {
  sitekey: string
  action?: string
  theme?: 'auto' | 'light' | 'dark'
  size?: 'normal' | 'flexible' | 'compact'
  callback?: (token: string) => void
  'expired-callback'?: () => void
  'error-callback'?: () => void
}

interface TurnstileApi {
  // Returns the widget's id, which remove() takes.
  render(container: HTMLElement, options: TurnstileRenderOptions): string
  remove(widgetId: string): void
}

interface Window {
  readonly __API_URL__?: string
  readonly __TURNSTILE_SITE_KEY__?: string
  // Set by Cloudflare's script once it has loaded.
  readonly turnstile?: TurnstileApi
}
