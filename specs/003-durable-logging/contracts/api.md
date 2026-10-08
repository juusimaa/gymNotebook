# API Contract: Durable Logging

## `POST /auth/token` (new)

Renews the caller's session token.

- **Auth**: bearer token, like every authenticated route. The existing bearer checks run first: an expired, revoked (stale `tv`), unconfirmed or link token gets the usual **401**, and a suspended account the usual **403** `account_suspended`.
- **Request body**: none.
- **200**: `{ "token": "<jwt>" }`, a token with the configured lifetime and the caller's `auth_time`. `Cache-Control: no-store`.
- **403** `{ "code": "renewal_refused" }`: the token has no `auth_time`, or its session started more than `Jwt:RenewalCapHours` ago. The current token stays valid until it expires.
- **429**: the per-IP `auth` rate limit.
- **503** `{ "code": "temporarily_unavailable" }`: the account lifecycle guard could not acquire shared access; retry later.

## `GET /workouts/{id}` (changed)

The workout gains `revision` (integer). The list (`GET /workouts`) doesn't need it and is unchanged.

## Writes to a workout (changed)

`PATCH /workouts/{id}`, `PUT /workouts/{id}/exercises`, `POST /workouts/{id}/sets`, `PATCH /workouts/{id}/sets/{setId}` and `DELETE` of a set:

- **Request**: may carry `expectedRevision` (integer, optional) in the body. For a `DELETE`, as the `expectedRevision` query parameter.
- **Success**: unchanged status; the response's workout (or, for set POST/PATCH, a new `revision` field) carries the new revision. Set DELETE remains 204 with no body; a following GET returns the new revision.
- **409** `{ "code": "page_changed", "revision": <current> }`: `expectedRevision` was sent and the page has moved on. Nothing is written.
- **404**: unchanged. A missing page and another user's page are indistinguishable, and that is checked before the revision, so a 409 never reveals another user's page.
- Without `expectedRevision`, writes behave exactly as today (last write wins) and still increase the revision.

`POST /workouts` (create) returns `revision: 1`.
