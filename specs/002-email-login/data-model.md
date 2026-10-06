# Data Model: Email Login and Open Signup

Draft for owner review, 2026-10-06. Snake-case column names are what `UseSnakeCaseNamingConvention()` produces.

## User (changed)

| Property | Column | Type | Rules |
| --- | --- | --- | --- |
| `Email` | `email` | `text`, not null | Trimmed and lowercased in C# before every write and lookup. Unique index. Max 254 characters (RFC 5321 path limit). Format check is deliberately loose (one `@`, something either side); the confirmation email is the real check. |
| `EmailVerifiedAt` | `email_verified_at` | `timestamptz`, null | Null = unconfirmed. Set once, by confirmation or by a completed reset; never cleared. The API exposes only a boolean. |
| `DisplayName` (renamed from `Username`) | `display_name` (renamed from `username`) | `text`, not null | **Unique index dropped**: a unique display name would leak which names exist at signup. Trimmed, 1–50 characters. Owner decision O1, 2026-10-06. |

Unchanged: `PasswordHash`, `TokenVersion` (also makes reset links single-use), `CreatedAt`, `PrivacyAccountId`, notice, consent and suspension columns.

No backfill: the migration requires an empty `users` table (plan D11).

## EmailSend (new)

| Property | Column | Type | Rules |
| --- | --- | --- | --- |
| `Id` | `id` | `bigint` identity | |
| `RecipientHash` | `recipient_hash` | `text`, not null, indexed | Base64 HMAC-SHA256 of the lowercased address, keyed with `Jwt:Secret`. |
| `SentAt` | `sent_at` | `timestamptz`, not null, indexed | Rows older than 24 hours are deleted on every slot claim. |

No foreign key to `users`: signup emails to addresses with no account are counted too, and the table must hold nothing that identifies a person.

## Link tokens (not stored)

| Purpose | Claims | Lifetime | Single use |
| --- | --- | --- | --- |
| `verify` | `sub`, `purpose`, `email`, `exp` | 48 h | Not needed: confirming is idempotent. |
| `reset` | `sub`, `purpose`, `tv`, `exp` | 1 h | Yes: completion bumps `TokenVersion`, so `tv` no longer matches. |

Signed with `Jwt:Secret` (HS256). The bearer handler rejects any token carrying `purpose`.

## Export (changed)

`account` gains `email` and `emailVerifiedAt`, both documented in `ExportFieldGuide.cs`. `username` becomes `displayName`, described as the name shown on the notebook's cover, not used to sign in.
