#!/bin/sh
set -eu

: "${API_URL:?API_URL must be configured}"

# TURNSTILE_SITE_KEY is optional: unset or empty leaves the Turnstile widget off
# on the signup and reset forms (src/screens/turnstileSiteKey.ts). It's the
# public half of the key pair, so writing it into a public file is fine.
{
  printf 'window.__API_URL__ = "%s";\n' "${API_URL%/}"
  printf 'window.__TURNSTILE_SITE_KEY__ = "%s";\n' "${TURNSTILE_SITE_KEY:-}"
} > /usr/share/nginx/html/config.js
