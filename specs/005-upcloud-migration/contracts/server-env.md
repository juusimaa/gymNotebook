# Contract: Production environment on the server

`/etc/gymnotebook/.env` (`root:gymdeploy 0640`) is the only place app secrets
exist in production. GitHub keeps only the deploy key, host and known-hosts
line (R6). Values marked **from Azure** are read once from the current Container
App (`az containerapp secret list --show-values` / `show`) so sessions and links
survive (FR-010, R8).

## `gym-api` (via `env_file`)

| Variable | Value | Source |
|---|---|---|
| `ConnectionStrings__Default` | `Host=<private host>;Port=<port>;Database=gymnotebook;Username=gymnotebook;Password=<pw>;SSL Mode=VerifyFull;Root Certificate=/etc/gymnotebook/upcloud-ca.pem;Maximum Pool Size=10` | new role (R2) |
| `Jwt__Secret` | unchanged | **from Azure** |
| `Jwt__ExpiryMinutes` | `30` | as Bicep |
| `CORS_ORIGINS` | `https://gymnotebook.fit` | the old `azurecontainerapps.io` origin is dropped |
| `PRIVACY_LIFECYCLE_ENABLED` | `false` | as Bicep |
| `Email__Backend` | `resend` | as Bicep |
| `EMAIL_FROM` | `Gym Notebook <no-reply@mail.gymnotebook.fit>` | as Bicep |
| `APP_URL` | `https://gymnotebook.fit` | as Bicep |
| `RESEND_API_KEY` | unchanged | **from Azure** |
| `TURNSTILE_SECRET_KEY` | unchanged | **from Azure** |
| `TURNSTILE_HOSTNAMES` | `gymnotebook.fit` | as Bicep |

Any other variable the current Bicep sets is copied as is. The implementing
PR diffs `infra/modules/container-app-api.bicep` against this table.

## Set in `compose.prod.yml`, not the env file

| Variable | Value | Why there |
|---|---|---|
| `FORWARDED_HEADERS_ENABLED` | `true` | only true because Caddy is in front and no port is published (R5) |
| `API_URL` (`gym-web`) | `https://api.gymnotebook.fit` | public, fixed by D3 |
| `TURNSTILE_SITE_KEY` (`gym-web`) | read from the env file | public half; unchanged from the GitHub variable |

## Not in production

`POSTGRES_*`, `INVITE_CODE`, Redis: as today.

## Rules

- Never printed by `deploy.sh` or CI. Edited with `sudo -e`, applied with
  `docker compose up -d` in `/opt/gymnotebook`.
- Production values never come from a laptop `.env` (PLAN.md § Configuration).
  The secrets copied from Azure already meet that rule.
