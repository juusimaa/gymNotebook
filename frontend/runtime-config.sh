#!/bin/sh
set -eu

: "${API_URL:?API_URL must be configured}"

# TURNSTILE_SITE_KEY is optional: unset or empty leaves the Turnstile widget off
# on the signup and reset forms (src/screens/turnstileSiteKey.ts). It's the
# public half of the key pair, so writing it into a public file is fine.
# DEMO_MODE_ENABLED shows "Try the demo" on sign-in only when exactly "true"
# (src/screens/demoModeEnabled.ts); set it together with the API's flag.
{
  printf 'window.__API_URL__ = "%s";\n' "${API_URL%/}"
  printf 'window.__TURNSTILE_SITE_KEY__ = "%s";\n' "${TURNSTILE_SITE_KEY:-}"
  printf 'window.__DEMO_MODE_ENABLED__ = "%s";\n' "${DEMO_MODE_ENABLED:-}"
} > /usr/share/nginx/html/config.js
