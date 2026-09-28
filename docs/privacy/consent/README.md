# Optional-details consent statement

The versioned statement that `GET /privacy/optional-details-statement` serves, and that a user consents to before the service stores a workout's title, location, notes or bodyweight (specs/001 user story 6, FR-029–FR-035, [data-model.md → Consent statement version](../../../specs/001-privacy-account-lifecycle/data-model.md#consent-statement-version--repository-artifact-not-ef-entity)). The API compiles every `*.json` file in this folder into its assembly (`GymNotebook.Api.csproj`, `OptionalDetailsConsentCatalog`). A change here takes effect only with a new build and deploy.

Unlike the notices in `../notices/`, this is real wording, not synthetic development content (tasks.md T089).

## Files

- **`index.json`** names the statements:
  - `current`: the version a grant must name.
  - `versions`: every version ever published, each with its `file`. A superseded version stays listed, and its file stays in the folder, for accountability (FR-034).
  - No `announcedSuccessor`. The API refuses to start if one is set.
- **`<version>.json`** has the same fields as a notice version: `version`, `effectiveAt`, `publishedAt`, `materialChangeSummary`, plain-text `sections`, and operator metadata (`owner`, `reviewDate`, `reviewEvidence`) that the API never serves.

## Changing the statement

Changing what the statement covers or permits is outside user story 6. It needs its own reviewed specification change, including what happens to consents given under the old wording (FR-034). Until then, publish nothing new here: a new `current` version would make every stored consent name a version that is no longer current.

The API validates the index and every listed file at startup, even with the feature flag off. `OptionalDetailsConsentCatalogTests` loads the same files in CI.
