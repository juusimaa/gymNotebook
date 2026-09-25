# Privacy notices

The versioned privacy notices that `GET /privacy/notice` serves (specs/001 [data-model.md → PrivacyNoticeVersion](../../../specs/001-privacy-account-lifecycle/data-model.md#privacynoticeversion--repository-artifact-not-ef-entity)). The API compiles every `*.json` file in this folder into its assembly (`GymNotebook.Api.csproj`, `PrivacyNoticeCatalog`). A change here takes effect only with a new build and deploy.

> **The versions here today are synthetic development content** (tasks.md T030). Their text says so. They exist to build and test the notice screens and the acknowledgement gate. Reviewed wording, with the controller, privacy contact and supervisory authority, replaces them before `PRIVACY_LIFECYCLE_ENABLED` is switched on in production (release gate T043). No placeholder may be published.

## Files

- **`index.json`** names the notices:
  - `current`: the version in effect.
  - `announcedSuccessor`: `null`, or a version with a future `effectiveAt`.
  - `versions`: every version ever published, each with its `file`. A superseded version stays listed, and its file stays in the folder, for accountability (FR-003).
- **`<version>.json`** holds one notice version:
  - `version`: 1–64 characters, unique, never reused or edited after publication.
  - `effectiveAt` and `publishedAt`: UTC instants.
  - `materialChangeSummary`.
  - `sections`: each has an `id`, a `heading` and plain-text `paragraphs`. No HTML: the UI renders them as text.
  - Operator metadata: `owner`, `reviewDate` and `reviewEvidence`. The API ignores these and never serves them.

Keep a section with the id **`contact`**: the account privacy screen links to it (`/privacy#notice-contact`).

## Publishing a new version

1. Add `<new-version>.json` with a future `effectiveAt`, list it in `versions`, and set `announcedSuccessor` to it. After deploy, the notice shows it as an announced change. Existing acknowledgements still count.
2. At `effectiveAt` the API switches to it by itself, with no deploy. From then on, the notebook gate asks every account that hasn't acknowledged it.
3. At the next convenient change, move the pointers: set `current` to the new version and `announcedSuccessor` to `null`. This changes no behaviour, because the clock already made the switch.

The API validates the index and every listed file at startup, even with the feature flag off, and refuses to start if anything is missing or malformed. `PrivacyNoticeCatalogTests` loads the same files in CI.
