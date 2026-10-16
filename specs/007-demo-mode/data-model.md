# Data Model: Demo Mode

**Feature**: [spec.md](spec.md) | **Research**: [research.md](research.md)

## Change to `users`

| Column | Type | Null | Meaning |
|--------|------|------|---------|
| `demo_expires_at` | `timestamptz` | yes | Null for a registered account. Set: a demo account, valid until this instant (creation + `Demo:LifetimeHours`). Never changed after creation; a reset doesn't extend it. |

Index: `ix_users_demo_expires_at` on `demo_expires_at` `WHERE demo_expires_at IS NOT NULL`. It serves the cleanup scan (`<= now()`) and the cap count (`> now()`), and skips every registered account.

Migration: `AddDemoAccounts`. It adds the column and the partial index. Existing rows stay null (registered), and no data changes.

## A demo account's fields

| Field | Value | Why |
|-------|-------|-----|
| `email` | `demo:` + random GUID (no `@`) | Unique (the existing index), but no email route accepts it as an address (research R7) |
| `display_name` | `Demo Lifter` | Shown on the cover |
| `password_hash` | BCrypt of a random GUID that is discarded | Required column. No one can know the password |
| `email_verified_at` | creation time | The session guards require a confirmed address |
| `token_version` | 0 | As at registration |
| `privacy_account_id` | random UUID | As at registration. Used in the deletion evidence lines |
| notice acknowledgement, consent | null | The visitor goes through both flows (spec clarification) |
| `demo_expires_at` | creation + 24 h | — |

## Lifecycle

```
          POST /auth/demo                     POST /account/demo/reset
 (none) ─────────────────▶ live demo ◀───────────────────────────┐
                              │  └───────────────────────────────┘
                              │ demo_expires_at passes
                              ▼
                       expired (every request 401)
                              │ DemoCleanupWorker → AccountDeletion.DeleteExpiredDemoAsync
                              ▼
                          (deleted, with notebook and evidence lines)
```

## Notebook data

There are no new tables. The seed is an in-memory `RestoreFile` (format version 2) imported through `NotebookRestore.ImportAsync`, so it lands in the existing tables (`exercises`, `workouts`, `workout_exercises`, `set_entries`, `templates`, `template_exercises`, `template_sets`) under the same validation as a restored backup. A reset deletes `workouts`, `templates` and `exercises` for the account, in that order (blocks and sets cascade), the same order as account deletion, then imports a new seed.
