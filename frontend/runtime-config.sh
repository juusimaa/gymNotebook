#!/bin/sh
set -eu

: "${API_URL:?API_URL must be configured}"

printf 'window.__API_URL__ = "%s";\n' "${API_URL%/}" \
  > /usr/share/nginx/html/config.js
