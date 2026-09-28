# Processing decision

The purpose-by-purpose record of what Gym Notebook processes, why, on what
lawful basis, whether any of it is health data, and whether consent is
needed. Required by specs/001 FR-004–FR-006
([contracts/operations.md → Maintained artifacts](../../specs/001-privacy-account-lifecycle/contracts/operations.md#maintained-artifacts),
tasks.md T041/T042).

| | |
| --- | --- |
| **Status** | **Draft for owner review.** Nothing below is decided until the Decision line of each purpose is filled in and signed. |
| **Controller** | Jouni Uusimaa, private individual, Finland |
| **Privacy contact** | jouni.uu@proton.me |
| **Supervisory authority** | Tietosuojavaltuutetun toimisto (Office of the Data Protection Ombudsman), Finland |
| **Owner / reviewer** | Jouni Uusimaa |
| **Version** | draft-1, 2026-09-25 |
| **Review date** | _(set when signed)_ |

**What this document is not.** It is not legal advice and does not certify
compliance. The facts were gathered from the code and from the read-only
provider inspection of 2026-09-24 (research R7). The lawful bases,
health-data assessments and consent conclusions are **proposals** drafted
for the owner. Where evidence is missing, the purpose carries a **blocking
finding**: that processing must not go live with the feature flag on until
the finding is resolved (FR-004).

## How to read each purpose

Each purpose records:

- **Information:** the fields and where they come from.
- **Where it lives and who can reach it.**
- **Necessity:** why the purpose can't be met with less.
- **Lawful basis (Art. 6):** proposed basis and rationale.
- **Health data (Art. 9):** the FR-005 assessment. A contract rationale alone
  never settles this.
- **Consent:** the FR-006 conclusion. "Not required" means the notice informs
  the user and nothing is asked. "Required" triggers the FR-007 amendment
  (T042) and blocks that processing.
- **Findings:** evidence still missing. **Blocking** findings stop that
  purpose from launching.
- **Decision:** owner's conclusion, date and evidence. Blank means undecided.

### Scope: why GDPR applies at all

The household exemption (Art. 2(2)(c)) covers purely personal activity. It
would cover the owner keeping only their own log. Gym Notebook lets other
people register (invite code) and hosts their data on third-party
infrastructure, so the operator is a controller for other people's data.
The CJEU reads the exemption narrowly (C‑101/01 _Lindqvist_, C‑212/13
_Ryneš_). This document assumes GDPR and the Finnish Data Protection Act
(1050/2018) apply.

---

## P1 — Account administration

**Information:**
- `Username`: chosen by the user. It may be a real name; nothing requires or forbids that.
- `PasswordHash`: a BCrypt hash. The password itself is never stored or logged.
- `TokenVersion`: revokes old sessions after a password change.
- `CreatedAt`.
- `PrivacyAccountId`: a random UUID used for restore suppression (research R5).
- `AcknowledgedPrivacyNoticeVersion` and `PrivacyNoticeAcknowledgedAt`: which notice the user has seen. This is not consent (FR-026).
- `SignInSuspendedAt`: set only when an account deletion's outcome is unknown after a restore (R6 Q2c).
- The invite code is checked at registration and never stored per user.

**Where it lives:** the `users` table in Neon PostgreSQL (`aws-eu-central-1`). Only the operator has database access. The API issues a signed JWT holding the user id and `TokenVersion`, valid for 30 minutes (`Jwt__ExpiryMinutes`).

**Necessity:** a username and password are the minimum needed for a private notebook that follows the user across devices. `TokenVersion`, `PrivacyAccountId`, acknowledgement and suspension each exist to meet another requirement: revocation, erasure surviving a restore, FR-026, and R6 fail-closed handling. No email, real name, phone number or date of birth is collected.

**Lawful basis (provisional owner choice, 2026-09-28):** Art. 6(1)(b) for the account and authentication information necessary to deliver the notebook the person requests. The service is free and invite-only; the owner approved the [registration service description](t043-notice-review.md#registration-service-description) and its placement beside Create account on 2026-09-28. Publication and confirmation against the actual registration flow remain. The basis depends on an actual service agreement and objective necessity, not merely calling the processing contractual ([EDPB Guidelines 2/2019](https://www.edpb.europa.eu/sites/default/files/files/file1/edpb_guidelines-art_6-1-b-adopted_after_public_consultation_en.pdf)). The notice-acknowledgement and restore/suspension records need their own purpose and basis decision; Art. 6(1)(c) remains a proposal for acknowledgement, not part of this approval.

**Health data:** none. Account fields describe the account, not the person's health.

**Consent (proposed):** not required.

**Findings:**
- **Blocking before release:** Publish the approved short service description before account creation, then confirm the Art. 6(1)(b) conclusion against the actual registration flow. Separately decide the acknowledgement and restore/suspension controls.

**Decision:** Owner provisionally chose Art. 6(1)(b) for essential account processing on 2026-09-28, subject to the service description above. The full P1 decision remains unsigned.

---

## P2 — Training log and progress

**Information:**
- **Workouts:** `Date`, `StartedAt`, `EndedAt`, `CreatedAt`.
- **Exercises:** `Name`, `NormalizedName`, `IsBodyweight`, `CreatedAt`.
- **Sets:** `SetNumber`, `Weight`, `Reps`, `IsWarmup`, and the exercise's position in the workout.
- **Progress:** the e1RM chart is calculated on request from stored sets (`ProgressMetric`, Epley formula) and never stored.

Exercise names are part of P2, including their free-text risk. Optional workout
title, location, notes and bodyweight are assessed separately in P3.

**Where it lives:** Neon PostgreSQL, one row set per user, scoped by `UserId`. Every `/workouts` and `/exercises` route returns 404 for another user's data.

**Necessity:** this is the service itself, a digital copy of a paper gym log. Nothing is derived beyond what the user asks to see.

**Lawful basis (provisional owner choice, 2026-09-28):** Art. 6(1)(b) for the core workout, exercise and set information needed to provide the requested log and progress chart, subject to P1's clear service-agreement condition. An Article 6 basis alone does not decide whether Article 9 also applies.

**Health data (owner decision, 2026-09-28):** **not health data in this context.** The owner, acting as controller and reviewer, weighed the points below and concluded that the core workout, exercise and set information does not reveal health status as Gym Notebook processes it. No Art. 9(2) condition is needed for P2. The rationale and the residual risk follow.
- *For:* Art. 4(15) covers data about physical or mental health that **reveals information about health status**.
  - The [Article 29 Working Party's 2015 annex](https://ec.europa.eu/justice/article-29/documentation/other-document/files/2015/20150205_letter_art29wp_ec_health_data_after_plenary_annex_en.pdf) distinguishes isolated lifestyle measurements from data tracked over time or combined with other information. It predates the GDPR and is guidance, not a ruling on this app.
  - Gym Notebook records lifts, sets and reps to track strength. It draws no health conclusions, gives no health advice and does no profiling.
- *Against:* the CJEU reads special categories broadly.
  - In C‑184/20 _OT_, data that reveals sensitive information **indirectly**, through inference, counted as special-category data.
  - In C‑21/23 _Lindenapotheke_, pharmacy orders counted as health data even without certainty about who they were for.
  - A long training history could support inferences, for example about an interruption from injury. That is a weaker link than P3's.
- *Art. 22:* no automated decisions or profiling take place. The notice must say so (FR-002).
- *Rationale for the conclusion:*
  - The records are lifestyle and fitness logging: which lift, how much weight, how many reps, on which date. They measure training performance, not health status, and the service uses them only to show the user their own log and e1RM chart.
  - The operator draws no health inferences, reads no users' logs in the ordinary course, gives no advice and does no profiling or analytics. The inferences in *Against* (such as an injury break) are speculative, and a gap in training has many ordinary explanations.
  - The fields most likely to reveal health status are handled separately. Bodyweight, notes, title and location are P3 and need explicit Art. 9(2)(a) consent. So the combination of a long P2 history with health-revealing details only exists for accounts that gave that consent.
  - Treating P2 as health data would leave explicit consent as the only realistic Art. 9(2) condition (see P3). Consent would then be a condition of the whole service, which conflicts with Art. 7(4), and withdrawing it would remove the notebook itself. That consequence is out of proportion to the weak link above.
- *Residual risk (accepted by the owner, 2026-09-28):*
  - The CJEU reads special categories broadly (C‑184/20 _OT_), so a supervisory authority could reach a different view on a long training history.
  - Exercise names are free text and can hold health details despite the notice's request. This risk was already accepted in the 2026-09-25 P3 decision.
  - Revisit this conclusion if the service ever analyses, derives or displays anything beyond the user's own log and progress chart, adds fields that describe the body or health, or shares notebook data with anyone other than the listed processors.

**Consent:** not required. P2 relies on Art. 6(1)(b) and, per the conclusion above, needs no Art. 9 condition. P3 consent does not cover P2 and P2 does not depend on it.

**Findings:**
- **Resolved 2026-09-28:** the Article 9 classification. The owner concluded not health data in this context, with the rationale and accepted residual risk recorded above, instead of waiting for an external data-protection review.
- **Remaining before release:** P1's service-agreement condition, which P2's Art. 6(1)(b) basis depends on (publish the approved registration description and confirm it against the actual registration flow).

**Decision:** Owner provisionally chose Art. 6(1)(b) for essential core processing on 2026-09-28, subject to P1's service-agreement condition.
- **Article 9 (owner, 2026-09-28):** not health data in this context; no Art. 9 condition and no consent. Residual risk accepted as recorded above. This replaces the earlier plan to seek a focused external review.
- The full P2 decision stays unsigned until P1's service-agreement condition is confirmed.

---

## P3 — Free text and bodyweight

**Information:** optional workout `Title`, `Location`, `Notes` and `BodyweightKg`. Exercise `Name` is required and assessed with the core notebook in P2; its free-text risk is discussed below. Nothing prompts the user for health information.

**Where it lives:** Neon PostgreSQL, like P2.

**Necessity:** optional context the paper log also had, such as where and how the session went. The service works without them.

**Lawful basis (owner decision, 2026-09-28):** Art. 6(1)(a), consent, for the four optional workout details (`Title`, `Location`, `Notes`, `BodyweightKg`). The separate affirmative Allow action and withdrawal flow implement that choice. Art. 9(2)(a) explicit consent is also required because these details can reveal health information. This decision does not cover exercise names, which remain part of P2's core notebook basis.

**Health data (assessment):** The owner chose to treat the four optional workout details as possible health information requiring explicit consent (Art. 9(2)(a)). Exercise names remain outside that consent with the residual risk recorded below.
- **Notes, titles and exercise names:** free text can hold anything, for example "left knee hurt, stopped after two sets", "back from surgery" or "rehab exercises".
  - That is data concerning health as soon as a user writes it.
  - The operator can't prevent it without either removing the field or reading users' notes.
  - `Location` can reveal a clinic or a physiotherapist's gym.
- **Bodyweight:** a recorded weight series is a physical measurement closely linked to health (weight change, obesity).
  - It is more plausibly health data than lift numbers, even with no height or BMI collected.
- **Available Art. 9(2) conditions:** the only realistic one for a private hobby service is **(a) explicit consent**.
  - (e) "manifestly made public" doesn't fit private notes.
  - (h), health care, doesn't fit either.
  - The Finnish Data Protection Act §6 exceptions cover insurers, health-care providers and similar bodies, not this service.

**Options for the owner:**

| Option | What it means | Consequence |
| --- | --- | --- |
| **A. Not health data** | Conclude that the operator doesn't collect health data: the fields aren't designed for it, the operator never reads or analyses it, and the notice tells users not to record health details. | Simplest, and no consent flow. The weakest legally: Art. 9 attaches to the data, not to the operator's intent, and the CJEU case law above cuts against it. Record the residual risk explicitly. |
| **B. Explicit consent** | Treat bodyweight and free text as possible health data processed under Art. 9(2)(a). | **Triggers T042:** these fields are blocked until an FR-007 amendment defines grant, refusal, withdrawal, the treatment of existing entries, and the transition for existing users. Refusing must still leave the rest of the notebook usable. This is the most work and the most defensible. |
| **C. Minimise** | Remove bodyweight, or remove or narrow `Notes`, so no field invites health information. | Removes most of the risk but loses features. Existing data needs a migration decision. Exercise names and titles remain free text, so some residual risk stays with A's reasoning. |

Options can be combined. For example, C for bodyweight plus A for the
remaining short free-text fields, or B for notes only.

**Consent:** **required** under the chosen option B (see Decision). T042 applies.

**Findings:**
- **Blocking (T042, FR-007).** Processing of these fields under consent needs an approved specification amendment, implemented and verified. It must define:
  - separate consent for this purpose only, given by an affirmative action and never bundled with the notice's "Continue";
  - refusal that leaves the rest of the notebook fully usable;
  - withdrawal as easy as granting;
  - versioned evidence of what was agreed and when;
  - what happens to entries already stored, both on refusal and on withdrawal;
  - the transition for existing accounts.
- **Accepted: processing already live.** These fields exist in production today, outside the feature flag, with no Art. 9 condition. The owner accepted this interim risk until the flag is switched on (see Decision). FR-035 governs the stored values afterwards.

**Decision:** Option **B**, explicit consent under Art. 9(2)(a), for the optional fields: `BodyweightKg`, `Title`, `Location` and `Notes`. Chosen by the owner, 2026-09-25.
- **Article 6 basis:** The owner chose consent under Art. 6(1)(a) for these four fields on 2026-09-28. This replaces the earlier proposed contract basis; the notice must state the same basis. The consent remains separate from notice acknowledgement.
- **Exercise names: outside consent** (owner, 2026-09-25). `Name` is required, since every set belongs to an exercise, so it can't depend on consent without breaking the notebook for anyone who refuses. Treated under option A's reasoning: the field names a lift, and the notice asks users not to put health details in names. **Residual risk accepted.**
- **Interim period: risk accepted** (owner, 2026-09-25). Until the feature flag is switched on (T084), production keeps accepting these details without an Art. 9 condition. Accepted for the small invite-only user base. No unflagged change is shipped. Existing values follow the spec's transition (FR-035): they are kept only if the account consents, and are cleared 30 days after enabling otherwise. Evidence: the FR-007 amendment, [spec.md FR-029–FR-035](../../specs/001-privacy-account-lifecycle/spec.md#functional-requirements), drafted 2026-09-25, awaiting approval. Sign when it is approved.

---

## P4 — Security and operational logs

**Information:**
- **API console logs** (`ContainerAppConsoleLogs_CL`, Log Analytics, swedencentral):
  - Default level `Information`, with `Microsoft.AspNetCore` at `Warning`.
  - The app can write `LifecycleFilter`'s "Guarded response write abandoned" diagnostic without an account identifier and the deletion evidence lines below.
  - EF Core logs SQL text but not parameter values (`EnableSensitiveDataLogging` is off).
  - Unhandled-exception lines may include a request path. Paths hold numeric workout and exercise ids.
- **Deletion log lines** (`AccountDeletion.cs`, R6 Q2b): intent, committed and rolled-back lines holding only the account's `PrivacyAccountId` and the deletion-boundary timestamp.
  - This is pseudonymous personal data (R6 Q2e). It exists so a database restore can't bring a deleted account back.
- **Container system logs** (`ContainerAppSystemLogs_CL`): platform events such as revisions, restarts and probes. No request data.
- **Frontend nginx access log:** turned off in PR #47 (T002). Lines from before that deployment (remote address, user agent, path, time) age out by about 2026-10-26. T075 verifies this.
- **Rate limiting:** the per-IP `auth` limiter and per-account password throttle hold counters **in memory only**. Nothing is stored.

**Where it lives:** Azure Log Analytics workspace `log-gymnote-prod-58dd`, with 30-day settings on the console/system tables that hold rows. Intended access is restricted to the operator; T076 verifies the effective permissions.

**Necessity:** error logs are needed to run the service. The deletion lines make an erasure survive a restore; without them the fallback path can't work.

**Lawful basis (proposed):**
- Operational and error logs: **Art. 6(1)(f)**, legitimate interest in keeping the service working and secure. The proposed balancing relies on minimal content, no notebook data or credentials, restricted access and at most 30 days; T075–T077 must verify those facts.
- Deletion lines: **Art. 6(1)(c)**, needed to comply with the Art. 17 erasure obligation, or 6(1)(f) as an alternative. FR-019 lets them stay until their original 30-day expiry after the account is gone. The deletion explanation must disclose this.

**Health data:** none is intended in the logs. T075 must scan stored content before this is treated as confirmed.

**Consent (proposed):** not required.

**Findings:**
- **Blocking (R7, T075–T077).** T069 set `immediatePurgeDataOn30Days: true` and pinned the twelve App\* tables to 30/30; a live table scan on 2026-09-28 found only `AzureActivity` and `Usage` at 90/90. Azure rejected 30 days for those two metadata tables. Their app-user content/routing, actual purge behavior, extra sinks and old rows still need T075–T077 evidence before a 30-day app-user claim holds.
- **Blocking (R7, T075).** No scan of stored log text for IPs, usernames, tokens or connection strings has been done yet. It is needed before the retention claim holds.
- *Non-blocking (R10).* Whether the Container Apps ingress keeps its own request logs with client IPs is unverified. It's likely not exposed to this project, but check before the notice states it.

**Decision:** _(owner, date, evidence)_

---

## P5 — Browser storage

**Information:** the session JWT in `localStorage` under `gymnotebook.token` (`frontend/src/auth/token.ts`). It holds the user id, `TokenVersion` and expiry, and is removed on sign-out or an invalid session. No other cookies, `localStorage` or `sessionStorage` keys, analytics or third-party scripts. Fonts are self-hosted since PR #48 (T001).

**Where it lives:** the user's own browser.

**Necessity:** without it the user would have to sign in again on every page load.

**Lawful basis (proposed):**
- Storing information on a device falls under the ePrivacy rule: Finland's Act on Electronic Communications Services (917/2014) §205.
- That rule allows storage without consent when it is **strictly necessary** for a service the user explicitly asked for. A sign-in token for a service the user logs into fits this.
- Under GDPR, the token belongs to P1: Art. 6(1)(b).

**Health data:** none.

**Consent (proposed):** not required. No cookie banner is needed, and FR-006 forbids adding one without cause.

**Findings:** none.

**Decision:** _(owner, date, evidence)_

---

## P6 — Discovered collection: suppliers and channels

These are recipients of the data in P1–P5 rather than purposes of their own.
Each must reach the notice's recipients and transfers section and
`suppliers.md` (T070) with evidence (FR-020). A supplier's name alone is not
evidence.

| Recipient | Role and data | Location | Evidence still needed |
| --- | --- | --- | --- |
| **Neon** | Processor: the whole database (P1–P3). On 2026-09-28 the project reported a 6-hour history window, no snapshots and one branch. | `aws-eu-central-1` (Frankfurt) | Applicable DPA/subprocessor list, internal durability copies, support-transfer paths and deletion assistance; see [suppliers.md](suppliers.md). |
| **Microsoft Azure** | Processor: runs the API and frontend containers, and holds the P4 logs. | swedencentral | DPA/Product Terms coverage for this subscription, transfer safeguards and actual log disposal; see [suppliers.md](suppliers.md). |
| **Cloudflare** | Authoritative DNS for `gymnotebook.fit` (`dawn`/`glen.ns.cloudflare.com`). **DNS-only, not proxied**, rechecked 2026-09-28: the A record resolved directly to the Azure frontend (20.240.228.206), the response had nginx's `server` header and no `cf-ray`, and public `/config.js` pointed the API directly to `azurecontainerapps.io`. Cloudflare sees DNS query metadata, usually from the visitor's resolver, rather than the page or API HTTP bodies. | global anycast | Applicable terms, role, DNS logging and transfers; see [suppliers.md](suppliers.md). **Keep the proxy off:** enabling it adds a new HTTP recipient and needs prior notice/inventory review. |
| **Proton Mail** | Holds messages sent to the privacy contact, including rights requests (FR-025). Proton AG is the operator's email provider. | Switzerland, Germany or Norway for encrypted mail storage per Proton's public policy; account-specific path unverified. Switzerland has an EU adequacy decision. | Applicable account terms, role, subprocessors and actual deletion; routine correspondence is scheduled for 12 months after closure under Q8. See [suppliers.md](suppliers.md) and [rights-requests.md](rights-requests.md). |
| **GitHub** | Holds source code and CI logs; no user data is intended. | — | Confirm that CI runs only against test containers, never production data. |

Google Fonts is no longer a recipient: T001/PR #48 self-hosted the fonts and recorded the browser network check.

**Lawful basis:** the basis of the purpose the data serves (P1–P5). Using a processor needs an Art. 28 agreement, not a separate basis. Proton's role for the contact mailbox should be confirmed: processor, or independent controller for its own purposes.

**Health data:** Neon holds P3 data, so whatever P3 concludes applies to Neon too. A rights request emailed to the contact mailbox may itself contain health details.

**Consent:** not required for these recipients as such.

**Findings:**
- **Blocking (FR-023/FR-024, T076).** Account-specific agreements, transfer paths, internal copies and deletion assistance remain unverified for the suppliers in [suppliers.md](suppliers.md).
- **Blocking (T076).** Cloudflare's DNS role/logging and Proton's terms/subprocessors/provider copies need review. Switzerland's adequacy decision covers transfers to Switzerland, not an unverified onward transfer.

**Decision:** _(owner, date, evidence)_

---

## Consent summary (T042)

| Purpose | Proposed consent conclusion | Blocks rollout? |
| --- | --- | --- |
| P1 Account administration | Provisionally not required for essential account processing; ancillary records still need a basis decision | Yes, final P1 decision is open |
| P2 Training log and progress | Not required: not health data in this context (owner, 2026-09-28); Article 6(1)(b) provisional | Yes, until P1's service-agreement condition is confirmed |
| P3 Optional workout details | **Required**: Article 6(1)(a) and Article 9(2)(a), owner 2026-09-25 and 2026-09-28 | Yes, pending final amendment and release review |
| P4 Logs | Not required | No, but retention findings block |
| P5 Browser storage | Not required (strictly necessary) | No |
| P6 Recipients | Not applicable | Supplier findings block |

**T042 outcome (updated 2026-09-28):** consent is required for P3's four
optional workout details. The FR-007 amendment
([spec.md FR-029–FR-035](../../specs/001-privacy-account-lifecycle/spec.md#functional-requirements))
and its implementation are recorded in [tasks.md](../../specs/001-privacy-account-lifecycle/tasks.md#phase-7a-user-story-6--choose-whether-to-record-optional-workout-details-priority-p1).
The feature remains behind the privacy flag. Final approval of the amendment,
the treatment of existing P3 values and the open P1/P2 sign-off remain release
gates; the recorded implementation alone does not sign off T042.

## Change control

Any new field, purpose, supplier or log source needs this record updated
first, together with the notice, `suppliers.md` and `retention.md`
([operations.md](../../specs/001-privacy-account-lifecycle/contracts/operations.md#maintained-artifacts)).
Keep superseded versions in Git history. Don't rewrite a signed decision;
add a new dated one.
