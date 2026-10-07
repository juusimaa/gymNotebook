# Data Model: Durable Logging

## Workout (changed)

| Field | Column | Type | Notes |
| --- | --- | --- | --- |
| `Revision` | `revision` | `integer not null default 1` | Increased by one on every write to the page or its sets (plan D3). Existing rows start at 1. |

Migration: `AddWorkoutRevision` adds the column with its default; nothing else. Review the generated migration for unintended changes before applying it.

No navigation properties change. No index is needed: every check is by primary key.

## Access token claims (changed)

| Claim | Set by | Kept by renewal | Notes |
| --- | --- | --- | --- |
| `sub`, `tv`, `exp`, `iat` | as today | `sub`, `tv` copied; `exp`, `iat` fresh | unchanged |
| `auth_time` (new) | sign-in, change-password, password reset: the moment the password was proven | copied unchanged | Unix seconds (RFC 7519 NumericDate, the OpenID Connect claim name). Renewal refuses once it is older than `Jwt:RenewalCapHours`. Tokens without it can't be renewed. |

Email-link tokens (`purpose` claim) are unchanged and never carry `auth_time`.
