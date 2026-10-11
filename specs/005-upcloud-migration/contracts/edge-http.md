# Contract: Edge behavior for Gym Notebook's hostnames

Served by the tracker's Caddy (`caddy:2.11.7`) from `deploy/gymnotebook.caddy`,
imported via `/etc/caddy/sites/*.caddy` (R4).

| Request | Answer |
|---|---|
| `http://gymnotebook.fit/*`, `http://api.gymnotebook.fit/*` | `308` to the `https://` URL (Caddy default) |
| `https://gymnotebook.fit/*` | proxied to `gym-web:80`; valid Let's Encrypt certificate |
| `https://api.gymnotebook.fit/*` | proxied to `gym-api:8080`; valid certificate |
| either host while the target container is down | `502` from Caddy; the tracker's hosts are unaffected |
| any other `Host` | not ours; handled by the tracker's config |

## Client address

- No `trusted_proxies`. Caddy **replaces** any client `X-Forwarded-For` with the
  TCP peer. `gym-api`, with `FORWARDED_HEADERS_ENABLED=true` and
  `ForwardLimit = 1`, therefore keys rate limits on the real client (FR-014).
- `gym-api` and `gym-web` publish no ports. The only route in is Caddy on the
  `edge` network.
- If Cloudflare's proxy is ever turned on, this contract and the privacy
  inventory change first (spec Assumptions).

## Logging

- No `log` directive in our site blocks: Caddy writes no per-request access
  line for our hostnames (D10). Error logs stay on.

## Not here

- No maintenance page (FR-011: not required). No `/health` special case: the
  deploy check reaches `/health` through the normal proxy.

## Checks

- CI: `caddy validate` on a small Caddyfile that imports
  `deploy/gymnotebook.caddy`, with `local_certs`.
- Rehearsal ([quickstart](../quickstart.md)): a spoofed
  `X-Forwarded-For` does not change the rate-limit bucket. A stopped `gym-api`
  gives 502 on our API while `subscriptionstrack.com` keeps answering.
