# Feature Specification: Privacy and Account Lifecycle

**Feature Branch**: `001-privacy-account-lifecycle`

**Created**: 2026-09-24

**Status**: Draft

**Input**: User description: "A good first use would be the GDPR-shaped ‘privacy and account lifecycle’ feature: privacy notice, consent decision, data export, account deletion, backup-retention rules, and processor inventory. It has genuine requirements and edge cases, rather than merely adding a screen."

## Clarifications

### Session 2026-09-24

- Q: How should existing users be shown a new or materially changed privacy notice? → A: Show once per notice version before notebook access, with a “Continue” action; record the version shown. This acknowledgement is not consent.
- Q: When an account is deleted, should identifying operational and security logs be erased immediately or retained until their existing 30-day expiry? → A: Allow necessary, access-restricted operational/security logs to remain until 30 days after collection, subject to the documented lawful-basis review; disclose this exception in the deletion explanation. Deletion does not restart the retention clock.
- Q: What file format should users receive when they export their notebook? → A: One JSON file containing all export data and field explanations.
- Q: Should usability acceptance require five test participants, or a documented walkthrough by you as the project owner? → A: Require the project owner's recorded walkthrough of notice, export and deletion, including mobile and keyboard use; no participant recruitment. Automated security and data-correctness checks remain separate requirements.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Understand how my information is used (Priority: P1)

As a prospective or existing user, I can read a plain-language privacy notice before creating an account and revisit it later, so I understand the service's data practices and how to exercise my rights.

**Why this priority**: Users need this information before giving the service their account and training information.

**Independent Test**: Open the notice while signed out and signed in; compare its disclosures with a reviewed sample of the data and processing inventories.

**Acceptance Scenarios**:

1. **Given** a signed-out visitor, **When** they visit the sign-in or registration experience, **Then** they can open the current privacy notice without creating an account or accepting it.
2. **Given** a signed-in user, **When** they open account privacy information, **Then** they can read the same current notice and find export, deletion, and privacy-contact instructions.
3. **Given** a reviewed processing inventory, **When** the notice is published, **Then** it identifies the controller and contact, purposes and lawful bases, information collected, recipients, international transfers and safeguards where applicable, retention periods, applicable rights, and the supervisory-authority complaint route.
4. **Given** a material change in processing, **When** it takes effect, **Then** the notice has a new version and effective date, existing users are informed before the change, and reading or acknowledging the notice is not recorded as consent.
5. **Given** a signed-in account has not acknowledged the current notice version, **When** the user attempts to enter their notebook, **Then** the notice is shown first with a “Continue” action; continuing records the version shown for that account and opens the notebook without recording consent.
6. **Given** an account has continued past the current notice version, **When** the user returns from any signed-in session, **Then** that version is not automatically shown again; a new version is shown before subsequent notebook access. Leaving without continuing does not suppress the notice on the next visit.

---

### User Story 2 - Establish whether consent is needed (Priority: P1)

As the service operator, I can review a purpose-by-purpose processing decision, so the service does not confuse informing users with obtaining valid consent or assume that all fitness information has the same legal classification.

**Why this priority**: The decision determines permissible processing and whether another user interaction is required before rollout.

**Independent Test**: Review the decision record against account information, workout/bodyweight information, free-text notes, security logs, and browser storage; verify every purpose has a recorded conclusion and reviewer.

**Acceptance Scenarios**:

1. **Given** the existing username/password and training-log service, **When** the operator reviews processing, **Then** each purpose has a documented lawful-basis rationale, necessity assessment, information categories, and dated approval or an explicit unresolved finding.
2. **Given** bodyweight, training history, or notes that may reveal health information, **When** the decision is reviewed, **Then** it addresses whether special-category rules apply and any additional condition needed; the decision cannot simply assume that calling the service a gym log excludes those rules.
3. **Given** a conclusion that consent is unnecessary for a purpose, **When** the user accesses that functionality, **Then** the service provides the relevant notice without asking for blanket privacy-policy consent.
4. **Given** a conclusion that consent is required, **When** rollout readiness is reviewed, **Then** that processing is blocked from rollout until the specification is amended with approved grant, refusal, withdrawal, existing-user, and retention behavior and those requirements are verified. This draft does not silently authorize a new consent workflow.

---

### User Story 3 - Take a copy of my notebook (Priority: P1)

As a signed-in user, I can download my account and notebook information in a reusable format, so I can inspect it or take it elsewhere without contacting the operator.

**Why this priority**: Users should retain practical control over the information they have entered, including before leaving the service.

**Independent Test**: Export a seeded account containing every supported field, an unused exercise, repeated exercise blocks, and unfinished workouts; compare all expected values and relationships with the download and check that another account's information is absent.

**Acceptance Scenarios**:

1. **Given** an authenticated user who verifies their current password, **When** they request an export, **Then** they receive one JSON file containing the information in FR-010 and embedded explanations of fields, units, dates, and relationships.
2. **Given** an account with no workouts, **When** it is exported, **Then** the export contains account information and explicit empty collections, rather than an error.
3. **Given** an expired session or incorrect password, **When** an export is requested, **Then** no personal download is disclosed and the user receives an actionable authentication message.
4. **Given** another session changes a workout during export, **When** the download completes, **Then** it represents one consistent point in time, identified in the download, without missing relationships or duplicated records.
5. **Given** a failed or interrupted export, **When** the user retries, **Then** they can obtain a fresh complete download and the failed attempt has not modified their notebook.

---

### User Story 4 - Delete my account and leave (Priority: P1)

As a signed-in user, I can permanently delete my account after verifying my identity and understanding the consequences, so I can leave without operator assistance.

**Why this priority**: Account deletion must remove access and personal records reliably, including when requests fail or overlap.

**Independent Test**: Delete an account with data and multiple active sessions; verify loss of access and removal of its active data while another account remains unchanged. Exercise cancellation, wrong passwords, failure, and retry separately.

**Acceptance Scenarios**:

1. **Given** a user considering deletion, **When** they open the action, **Then** they see the affected information, irreversibility, export option, backup deadline, and any reviewed retention exception before confirmation.
2. **Given** a user cancels or supplies an incorrect password, **When** the action ends, **Then** their account and notebook remain unchanged.
3. **Given** a user confirms with the correct current password, **When** deletion succeeds, **Then** their active account, exercises, workouts, exercise blocks, sets, and feature-created personal records are removed, every existing session loses access, and a completion message distinguishes active-data deletion from backup expiry and the restricted log-retention exception in FR-019.
4. **Given** deletion fails before completion, **When** the user receives a response, **Then** the service does not claim success or leave a partially erased usable notebook; it supplies a safe retry or privacy-contact path.
5. **Given** deletion completed but the response was lost, **When** the old session retries, **Then** no data is recreated, no other account is affected, and the client explains that the session no longer authorizes access without falsely certifying deletion from that response alone.
6. **Given** an export or notebook edit overlaps confirmed deletion, **When** deletion completes, **Then** subsequent access and new writes are denied and unfinished export delivery is cancelled. Information already delivered to the user's device cannot be recalled.
7. **Given** an account has an identifying security-log entry collected 20 days before deletion whose retention is justified by the documented review, **When** the account is deleted, **Then** the entry remains restricted to its reviewed purpose and expires no later than 10 days afterwards; an entry without a continued-retention justification is erased or anonymized on deletion.

---

### User Story 5 - Keep retention and suppliers accountable (Priority: P1)

As the operator, I maintain a verifiable retention schedule and supplier inventory, so the privacy notice reflects actual processing and restored backups do not revive deleted accounts.

**Why this priority**: User-facing deletion is incomplete if retained copies and external processing are unaccounted for.

**Independent Test**: Review a representative inventory and retention schedule, then restore a pre-deletion backup into an isolated environment and verify deleted records are removed before access is allowed.

**Acceptance Scenarios**:

1. **Given** a supplier handles service personal information, **When** the inventory is reviewed, **Then** its role, purpose, information categories, processing locations, contractual evidence, transfer arrangements where relevant, retention/deletion arrangements, owner, and review date are recorded.
2. **Given** an account was deleted today, **When** the retention schedule is evaluated, **Then** remaining backup copies expire within 30 calendar days of deletion and are unavailable for ordinary product use in the meantime.
3. **Given** a backup predates an account deletion, **When** it is restored, **Then** deletion records are applied before the restored service becomes available, including removal of the deleted account and its credentials and notebook information.
4. **Given** an unknown provider retention setting, missing agreement, or unverified transfer arrangement, **When** rollout readiness is evaluated, **Then** the gap has an owner and blocks rollout of the affected processing until verified or removed.
5. **Given** a supplier or purpose changes, **When** the operator proposes the change, **Then** the inventory, decision record, retention schedule, and notice are reconciled before the change takes effect.

### Edge Cases

- Existing users have no historical notice acknowledgement or consent record; migration must not fabricate either.
- Notes, exercise names, titles, and locations can contain personal or health information; export and deletion cover free text as well as numeric training data.
- Missing bodyweight, nullable set weight, unfinished workouts, non-ASCII text, warm-up sets, repeated exercises, and local dates around midnight retain their meaning in exports.
- Session expiry during an action never discloses data or bypasses password verification; repeated password failures are limited without revealing another account's existence.
- Deletion and a concurrent login, password change, export, or workout save must not restore access or create orphaned personal data.
- A newly registered account reusing a deleted username has a distinct identity and cannot inherit that notebook or be removed by replaying the former account's deletion.
- A provider outage or interrupted restore must not allow a partially restored service to expose deleted records.
- Copies downloaded by the user are outside service-controlled retention; application-owned local information on an active device is cleared when deletion or invalidation is observed. Offline devices cannot be promised immediate remote erasure.
- A backup retention period shorter than 30 days is acceptable; indefinite snapshots, development copies, and manual exports cannot bypass the maximum.
- A rights request from a person unable to sign in follows the notice's contact route with proportionate identity verification; self-service does not replace this route.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The service MUST provide a public, readable privacy notice accessible before registration and from signed-in account privacy controls. It MUST support keyboard navigation and the existing mobile experience.
- **FR-002**: The notice MUST contain the disclosures in Story 1, scenario 3, accurately distinguish required and optional information and consequences of not providing it, and explain whether automated decision-making or profiling occurs.
- **FR-003**: Every published notice MUST have a version and effective date. Prior wording MUST remain available to the operator for accountability. Material changes MUST be communicated before taking effect; mere display or acknowledgement MUST NOT count as consent.
- **FR-004**: The operator MUST produce a reviewed processing decision covering account administration, notebook storage and progress calculations, security/operational logs, browser storage, and any discovered third-party collection. Each purpose MUST have an identified lawful basis and rationale or a rollout-blocking unresolved finding.
- **FR-005**: The decision MUST explicitly assess workout, bodyweight, and free-text information for possible health-data treatment, recording the rationale and any additional special-category condition. A contract rationale alone MUST NOT be treated as resolving special-category requirements.
- **FR-006**: This feature MUST deliver a consent decision record. It MUST NOT introduce a blanket acceptance checkbox, analytics consent banner, or implicit consent from continued use. A finding that consent is necessary MUST trigger a reviewed specification amendment and prevent rollout of that processing until satisfied.
- **FR-007**: Any consent-flow amendment MUST define separate purposes, affirmative grant, refusal, withdrawal as accessible as grant, versioned evidence, effects on existing information, and existing-user transition before implementation. This is a decision gate, not an implemented consent feature in this draft.
- **FR-008**: Signed-in users MUST be able to initiate their own export and account deletion from account privacy controls without operator assistance. Both actions MUST verify the current password; ordinary login and invite-based registration behavior remain unchanged.
- **FR-009**: Export and deletion MUST operate only on the authenticated account. Unauthorized access MUST reveal no personal information; requests targeting specific missing or unowned notebook resources MUST preserve the established indistinguishable not-found behavior. Password attempts MUST be rate limited, and errors MUST NOT expose secrets or account existence.
- **FR-010**: The export MUST include account identity, username and creation time; every owned exercise with name, classification and creation time; every workout's date, start/end times, title, location, notes, bodyweight and creation time; ordered exercise blocks and their relationships; and all sets with order, weight, repetitions and warm-up status. It MUST also include user-linked privacy records introduced by this feature that are still retained.
- **FR-011**: The export MUST be a single valid JSON file containing all required export data, its format version and snapshot time, and embedded field explanations covering units, null values, identifiers, relationships and time interpretation. It MUST preserve associations and user-entered values. Computed progress charts need not be exported because their underlying records are included.
- **FR-012**: Downloads MUST exclude passwords, password hashes, authentication tokens, security revocation values, service credentials, and other users' data. Any security-sensitive omission from a broader access request MUST receive separate operator review rather than automatic denial.
- **FR-013**: An export MUST contain a consistent snapshot and MUST NOT silently truncate large notebooks. Failure MUST leave the notebook unchanged and offer a retry. Downloads MUST require authenticated delivery, MUST NOT use publicly accessible links, and any service-side temporary copies MUST expire within 24 hours or upon account deletion, whichever comes first.
- **FR-014**: Before deletion, the user MUST receive the consequences in Story 4, scenario 1, an optional export action, and separate explicit confirmation. The explanation MUST disclose that necessary, access-restricted operational/security logs may remain until 30 days after collection under FR-019. Completing export MUST NOT be required to delete. Cancellation or failed verification MUST change nothing.
- **FR-015**: Successful deletion MUST remove all active account and notebook information, unused exercises, feature-created personal records and temporary exports, except the necessary restricted logs in FR-019 and the narrowly defined retention records in FR-020 and FR-021. Success MUST NOT be reported before active deletion completes.
- **FR-016**: Deletion MUST revoke all sessions, prevent overlapping operations from creating or disclosing new data after completion, and clear application-owned personal state on the initiating device. Other active clients MUST clear such state when they next observe invalidation. Already downloaded files remain under user control.
- **FR-017**: Deletion MUST be safe to retry and MUST either complete active removal or leave the pre-deletion active notebook intact and protected. A rejected expired-session retry MUST NOT be presented as evidence that deletion succeeded. Reusing a username MUST never reconnect deleted data.
- **FR-018**: The operator MUST maintain a retention schedule covering active records, logs, exports, backups, snapshots, replicas and any service-controlled non-production copies. Every category MUST have a purpose, maximum duration, start event, deletion mechanism, owner and verification evidence.
- **FR-019**: As proposed product limits, deleted data in backup copies MUST expire within 30 calendar days of deletion; identifiable operational/security logs MUST expire within 30 calendar days of collection; temporary exports MUST follow FR-013. After account deletion, only operational/security logs whose continued retention is justified by the documented lawful-basis and necessity review MAY remain until their original 30-day expiry. They MUST be access-restricted to the reviewed operational/security purposes and MUST NOT preserve notebook content or credentials. Log information not justified for continued retention MUST be erased or anonymized on account deletion. Active notebook data remains until the user removes it or deletes the account. Account deletion, restoring or copying information MUST NOT restart any retention clock.
- **FR-020**: The service MAY retain the minimum deletion evidence needed to prevent resurrection: an account identity reference distinct from reusable username, deletion time, and restore-suppression expiry. It MUST exclude credentials and notebook content, remain access-restricted, and expire no later than 31 calendar days after deletion. Restore sources older than the backup limit MUST be destroyed or made unusable before this evidence expires.
- **FR-021**: Any exceptional retention beyond these limits MUST be supported by a specific reviewed obligation or claim, documented information scope, access restriction, owner, review date and end condition. Blanket indefinite retention is forbidden. No such exception is assumed for this draft; discovering one requires a reviewed specification and notice update before rollout.
- **FR-022**: Backups awaiting expiry MUST be restricted to recovery use. Every restore MUST reconcile all unexpired deletion evidence before user access resumes and MUST fail closed if reconciliation cannot be verified. The operator MUST demonstrate this with an isolated restore exercise.
- **FR-023**: The supplier inventory MUST distinguish processors, subprocessors and other recipient roles and record the fields in Story 5, scenario 1. It MUST cover hosting, database, logs, network delivery/DNS where relevant, external resources and any support service receiving personal information; a supplier name alone is insufficient evidence.
- **FR-024**: Before rollout, the operator MUST verify provider settings and contracts against the notice, retention schedule and inventory, including deletion assistance and transfer safeguards where applicable. Unknown settings MUST be marked unverified, never described as confirmed guarantees.
- **FR-025**: The notice MUST provide a monitored privacy contact and instructions for access, correction, erasure, restriction, objection, portability where applicable, and complaints. The operator MUST track received requests and respond within one calendar month; a permitted extension or refusal requires timely explanation and a recorded reason.
- **FR-026**: On their next signed-in visit, users who have not acknowledged the current privacy notice version MUST see it before notebook access, with a “Continue” action. Continuing MUST record the version shown for that account and permit notebook access; the same version MUST NOT be automatically shown again across sessions. Leaving without continuing MUST leave acknowledgement unchanged. A new notice version MUST repeat this flow. The recorded version is notice acknowledgement only, MUST be included in export and removed on account deletion, and MUST NOT be treated as consent. Historical acknowledgement or consent MUST NOT be invented. If the decision changes the permitted processing of existing data, the approved amendment in FR-007 MUST define its treatment before rollout; notice acknowledgement alone does not authorize that change.
- **FR-027**: Product controls MUST show progress, completion, authentication failure and recoverable failure states without exposing private data or internal exception details. Authentication failure MUST NOT be misrepresented as successful export or deletion.
- **FR-028**: Delivery MUST include reviewed notice content, processing decision, retention schedule, supplier inventory and restore procedure as maintained artifacts with an owner and review date. The relevant product, operating and UI documentation MUST be updated with the corresponding implementation; this specification alone does not establish compliance or operational readiness.

### Key Entities *(include if feature involves data)*

- **Account and notebook information**: The user's account, exercises, workouts, ordered exercise appearances and sets, including free text and bodyweight. Ownership connects all of it to one account.
- **Privacy notice version**: Published wording, effective date, version and material-change description; distinct from any consent evidence.
- **Notice acknowledgement**: The latest notice version shown and acknowledged through “Continue”, linked to the account to avoid repeat prompts across sessions. It does not establish consent or prove the notice was read.
- **Processing decision**: Purpose, information categories, necessity, lawful basis, health-data assessment, consent conclusion, rationale, reviewer, date and unresolved findings.
- **Export**: A single JSON file containing a user's complete permitted data snapshot, embedded field explanations, format version, snapshot time and any temporary-copy expiry.
- **Deletion evidence**: Minimal restricted information used to suppress a deleted account during restoration, with a bounded expiry; not a retained notebook.
- **Retention rule**: Information category, purpose, triggering event, maximum duration, disposal behavior, owner and verification evidence.
- **Supplier inventory entry**: Provider and role, purpose, information handled, processing locations, agreement and transfer evidence, retention/deletion arrangements and review ownership.
- **Rights request record**: Request type and receipt date, proportionate identity-verification outcome, deadline, response and closure; its own retention is reviewed under FR-018 without retaining request content indefinitely.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every sign-in/registration entry point and the signed-in account experience exposes the privacy notice in at most two user actions; all Story 1 disclosure checks pass before publication. All first-visit, repeat-visit, new-version, abandoned-notice and cross-session cases follow FR-026, with no consent record created by “Continue”.
- **SC-002**: Every identified processing purpose and recipient has a reviewed decision/inventory entry; zero unresolved lawful-basis, health-data, agreement, transfer or retention findings remain for processing released with this feature.
- **SC-003**: For a reference notebook of 1,000 workouts, 10 exercise blocks per workout and 10 sets per block, a complete export as one valid JSON file is available within 60 seconds under a documented normal connection and service load. The file includes the field explanations, format version and snapshot time. Field-by-field comparison shows 100% of required information and zero other-user records or credentials.
- **SC-004**: The project owner MUST complete and record a walkthrough of the privacy notice, export and account-deletion flows, covering mobile use and keyboard-only navigation. Each flow MUST be discoverable and completable without assistance in under three minutes excluding download time. The walkthrough MUST confirm that notice acknowledgement is not consent and that the deletion explanation communicates active-data removal, the backup deadline and the restricted log-retention exception. Record the date, device/browser, steps and observed pass/fail outcomes; all three flows MUST pass both mobile and keyboard coverage before acceptance. No participant recruitment is required. This walkthrough does not replace the separate security, data-correctness or restore checks.
- **SC-005**: All deletion acceptance cases pass, including multiple sessions, wrong passwords, cancellation, concurrent writes/exports and lost responses. Successful deletion of the reference notebook completes within 60 seconds and leaves zero active account or notebook records while preserving every control-account record. Any retained identifying operational/security logs meet the reviewed necessity, access restriction and original-expiry conditions in FR-019, and the deletion explanation discloses this exception.
- **SC-006**: A restore exercise using a pre-deletion backup exposes zero deleted-account records after access is enabled. Verification evidence demonstrates enforcement of the 24-hour export, 30-day backup/log, and 31-day deletion-evidence limits, including boundary and expiry cases.
- **SC-007**: Every published notice and operational artifact has an owner, version or review date and supporting evidence; all rights-request practice cases receive a response or a justified extension notice within the one-calendar-month deadline.

## Assumptions

- This is a specification for new work, not a claim that the deployed service already meets the requirements or a certification of GDPR compliance. Owner review remains necessary before implementation under the project constitution.
- **Consent scope confirmed by owner on 2026-09-24**: Document the lawful-basis decision and add no consent prompt unless the review establishes a need. No final lawful basis or health-data classification is asserted here.
- The existing username/password account, invite gate, user ownership and session-revocation conventions remain. No email collection, email delivery, password reset, marketing, advertising or analytics is introduced.
- The proposed 24-hour, 30-day and 31-day limits and performance targets are product requirements for review, not statutory GDPR periods or verified provider guarantees. Provider capability must be established during planning and operational validation; conflicts require an explicit specification change.
- Microsoft Azure and Neon are initial inventory candidates supported by repository deployment context. Actual supplier roles, network providers, locations, agreements, subprocessors and retention settings require verification; this task performs no cloud audit.
- Actual controller identity, monitored contact details and applicable supervisory authority are operator-supplied publication dependencies. The notice cannot be published with placeholders. An additional administration interface is unnecessary; reviewed operational records suffice.
- Self-service export covers account and notebook information, not every possible access-rights case involving security logs or correspondence. The contact workflow handles that broader scope. Import, direct transfer to another service, account recovery, a deletion grace period and child-specific registration are outside this feature.
- The data inventory reflects the current account and notebook model. Any additional personal collection discovered in planning must be included in the notice, inventory, retention and rights handling before rollout.

### Reference Basis

These references inform review topics; the product deadlines and acceptance targets above are proposed requirements. In particular, lawful-basis selection and consent conditions require purpose-specific assessment, and potential health information requires additional review. The feature does not equate a privacy notice with consent or promise immediate destruction of every backup.

- [GDPR, Regulation (EU) 2016/679](https://eur-lex.europa.eu/legal-content/EN/TXT/PDF/?uri=CELEX%3A32016R0679): review Articles 5–7, 9, 12–13, 15–22, 28 and 44 onward for the processing decision, notice, rights and supplier review.
- [EDPB Guidelines 05/2020 on consent](https://www.edpb.europa.eu/documents/guideline/guidelines-052020-on-consent-under-regulation-2016679_en): review whether consent is appropriate and how it differs from a notice acknowledgement.
- Repository context reviewed: `.specify/memory/constitution.md`, `PLAN.md` data model/auth sections, current account and notebook entity definitions, frontend authentication storage, and infrastructure log-retention declaration. These establish scope, not live deployment evidence.
