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

**Lawful basis (proposed):** Art. 6(1)(b), necessary to perform the service the user signs up for.
- *Caveat:* the app has no written terms of service, and it's free and invite-only. Art. 6(1)(b) still works where there is an agreement to provide a service, but it applies only to what is **objectively necessary** for that service (EDPB Guidelines 2/2019).
- *Alternative:* Art. 6(1)(f), legitimate interests. It needs a written balancing test.
- The acknowledgement fields record that the notice was shown (Art. 12–13 transparency and Art. 5(2) accountability). Art. 6(1)(c) is a plausible basis for them.

**Health data:** none. Account fields describe the account, not the person's health.

**Consent (proposed):** not required.

**Findings:**
- *Non-blocking.* Decide between 6(1)(b) and 6(1)(f). If 6(1)(b), consider whether a short terms-of-use statement is needed to show what the service is.

**Decision:** _(owner, date, evidence)_

---

## P2 — Training log and progress

**Information:**
- **Workouts:** `Date`, `StartedAt`, `EndedAt`, `CreatedAt`.
- **Exercises:** `Name`, `NormalizedName`, `IsBodyweight`, `CreatedAt`.
- **Sets:** `SetNumber`, `Weight`, `Reps`, `IsWarmup`, and the exercise's position in the workout.
- **Progress:** the e1RM chart is calculated on request from stored sets (`ProgressMetric`, Epley formula) and never stored.

Free-text fields and bodyweight are assessed separately in P3.

**Where it lives:** Neon PostgreSQL, one row set per user, scoped by `UserId`. Every `/workouts` and `/exercises` route returns 404 for another user's data.

**Necessity:** this is the service itself, a digital copy of a paper gym log. Nothing is derived beyond what the user asks to see.

**Lawful basis (proposed):** Art. 6(1)(b), the core of the service. Same caveat as P1.

**Health data (proposed assessment):** **not health data in this context**, with a stated risk.
- *For:* Art. 4(15) covers data about physical or mental health that **reveals information about health status**.
  - The Article 29 Working Party's 2015 annex on health data in apps separates lifestyle and fitness data from health data. Raw activity data counts as health data when it is used, or can reasonably be used, to draw conclusions about a person's health. *Verify this citation before signing.*
  - Gym Notebook records lifts, sets and reps to track strength. It draws no health conclusions, gives no health advice and does no profiling.
- *Against:* the CJEU reads special categories broadly.
  - In C‑184/20 _OT_, data that reveals sensitive information **indirectly**, through inference, counted as special-category data.
  - In C‑21/23 _Lindenapotheke_, pharmacy orders counted as health data even without certainty about who they were for.
  - A long training history could support inferences, for example about an interruption from injury. That is a weaker link than P3's.
- *Art. 22:* no automated decisions or profiling take place. The notice must say so (FR-002).

**Consent (proposed):** not required, if the assessment above is accepted.

**Findings:**
- *Non-blocking.* The owner should confirm the assessment, and ideally have it checked by someone with data-protection expertise.

**Decision:** _(owner, date, evidence)_

---

## P3 — Free text and bodyweight

**Information:** workout `Title`, `Location`, `Notes`, `BodyweightKg`, and free-form exercise `Name`s. All optional and user-typed. Nothing prompts the user for health information.

**Where it lives:** Neon PostgreSQL, like P2.

**Necessity:** optional context the paper log also had, such as where and how the session went. The service works without them.

**Lawful basis (proposed):** Art. 6(1)(b) for the fields as a feature of the service. This does **not** settle Art. 9 (FR-005).

**Health data (assessment):** **undecided. This is the key open question of US2.**
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
- **Exercise names: outside consent** (owner, 2026-09-25). `Name` is required, since every set belongs to an exercise, so it can't depend on consent without breaking the notebook for anyone who refuses. Treated under option A's reasoning: the field names a lift, and the notice asks users not to put health details in names. **Residual risk accepted.**
- **Interim period: risk accepted** (owner, 2026-09-25). Until the feature flag is switched on (T084), production keeps accepting these details without an Art. 9 condition. Accepted for the small invite-only user base. No unflagged change is shipped. Existing values follow the spec's transition (FR-035): they are kept only if the account consents, and are cleared 30 days after enabling otherwise. Evidence: the FR-007 amendment, [spec.md FR-029–FR-035](../../specs/001-privacy-account-lifecycle/spec.md#functional-requirements), drafted 2026-09-25, awaiting approval. Sign when it is approved.

---

## P4 — Security and operational logs

**Information:**
- **API console logs** (`ContainerAppConsoleLogs_CL`, Log Analytics, swedencentral):
  - Default level `Information`, with `Microsoft.AspNetCore` at `Warning`.
  - The app writes one information line of its own: `LifecycleFilter`'s "Guarded response write abandoned", which carries no identifier.
  - EF Core logs SQL text but not parameter values (`EnableSensitiveDataLogging` is off).
  - Unhandled-exception lines may include a request path. Paths hold numeric workout and exercise ids.
- **Planned deletion log lines** (US4, R6 Q2b): intent, committed and rolled-back lines holding only the account's `PrivacyAccountId` and the deletion-boundary timestamp.
  - This is pseudonymous personal data (R6 Q2e). It exists so a database restore can't bring a deleted account back.
- **Container system logs** (`ContainerAppSystemLogs_CL`): platform events such as revisions, restarts and probes. No request data.
- **Frontend nginx access log:** turned off in PR #47 (T002). Lines from before that deployment (remote address, user agent, path, time) age out by about 2026-10-26. T075 verifies this.
- **Rate limiting:** the per-IP `auth` limiter and the planned per-account password throttle hold counters **in memory only**. Nothing is stored.

**Where it lives:** Azure Log Analytics workspace `log-gymnote-prod-58dd`, 30-day retention on the tables that hold rows. Only the operator's Azure account can read it.

**Necessity:** error logs are needed to run the service. The deletion lines make an erasure survive a restore; without them the fallback path can't work.

**Lawful basis (proposed):**
- Operational and error logs: **Art. 6(1)(f)**, legitimate interest in keeping the service working and secure. Balancing: content is minimal, there is no notebook content or credentials, access is limited to the operator, and entries are kept at most 30 days.
- Deletion lines: **Art. 6(1)(c)**, needed to comply with the Art. 17 erasure obligation, or 6(1)(f) as an alternative. FR-019 lets them stay until their original 30-day expiry after the account is gone. The deletion explanation must disclose this.

**Health data:** none. No notebook content reaches the logs.

**Consent (proposed):** not required.

**Findings:**
- **Blocking (R7, T075).**
  - `immediatePurgeDataOn30Days` isn't set, so Log Analytics may keep data about 31 days.
  - The App\*, `Usage` and `AzureActivity` tables are at 90 days.
  - Pin and verify these before claiming 30 days.
- **Blocking (R7).** No scan of stored log text for IPs, usernames, tokens or connection strings has been done yet. It's needed before the retention claim holds.
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
| **Neon** | Processor: the whole database (P1–P3). 6-hour history window, no snapshots, one branch (R7). | `aws-eu-central-1` (Frankfurt) | DPA and sub-processor list; internal durability copies; any US transfer path (for example support access) and its safeguard |
| **Microsoft Azure** | Processor: runs the API and frontend containers, and holds the P4 logs | swedencentral | DPA/Product Terms coverage for this subscription; transfer safeguards |
| **Cloudflare** | Authoritative DNS for `gymnotebook.fit` (name servers `dawn`/`glen.ns.cloudflare.com`). **DNS-only, not proxied**, checked 2026-09-25: the A record resolves directly to the Azure frontend (20.240.228.206), and responses carry nginx's `server` header with no `cf-ray`. Cloudflare therefore sees DNS queries, usually from the visitor's resolver rather than the visitor, and none of the page or API traffic. The API is called on its `azurecontainerapps.io` address, not through this domain. | global anycast | Cloudflare's DPA and role for DNS; whether it is also the registrar (that concerns the controller's own data, not users'). **Keep the proxy off:** switching it on makes Cloudflare a processor for all frontend traffic, and would need this record, the notice and `suppliers.md` updated first |
| **Proton Mail** | Holds messages sent to the privacy contact, including rights requests (FR-025). Proton AG is the operator's email provider. | Switzerland (EU adequacy decision 2000/518/EC, confirmed in the Commission's January 2024 review) | The account's terms and data location; whether any sub-processor sits outside Switzerland or the EU; retention of rights-request mail (T073) |
| **GitHub** | Holds source code and CI logs. No user data. | — | Confirm that CI runs only against test containers, never production data |
| **Google Fonts** | **Removed** by self-hosting in PR #48 (T001). | — | Confirm in a real browser that no `fonts.googleapis.com`/`fonts.gstatic.com` request remains, then drop this row |

**Lawful basis:** the basis of the purpose the data serves (P1–P5). Using a processor needs an Art. 28 agreement, not a separate basis. Proton's role for the contact mailbox should be confirmed: processor, or independent controller for its own purposes.

**Health data:** Neon holds P3 data, so whatever P3 concludes applies to Neon too. A rights request emailed to the contact mailbox may itself contain health details.

**Consent:** not required for these recipients as such.

**Findings:**
- **Blocking (FR-020, T070).** Supplier evidence for Neon and Azure.
- *Non-blocking.* Cloudflare is DNS-only (checked 2026-09-25). Record its DPA in `suppliers.md` (T070), and treat "proxy off" as a setting to re-check before release.
- *Non-blocking.* Proton as the contact channel: Switzerland has an adequacy decision, so no further transfer safeguard is needed for Proton itself. Confirm the terms and sub-processors when writing `suppliers.md` (T070).
- *Non-blocking.* Google Fonts browser check.

**Decision:** _(owner, date, evidence)_

---

## Consent summary (T042)

| Purpose | Proposed consent conclusion | Blocks rollout? |
| --- | --- | --- |
| P1 Account administration | Not required | No |
| P2 Training log and progress | Not required, if the health-data assessment is accepted | No |
| P3 Free text and bodyweight | **Required**: option B, owner 2026-09-25 | **Yes**, until the FR-007 amendment is approved and implemented |
| P4 Logs | Not required | No, but retention findings block |
| P5 Browser storage | Not required (strictly necessary) | No |
| P6 Recipients | Not applicable | Supplier findings block |

**T042 outcome (2026-09-25):** consent is required for P3. Processing of
bodyweight and free text under the lifecycle feature is **halted** until an
FR-007 specification amendment is approved and implemented. The amendment
must also decide the treatment of the P3 data already in production (see P3
findings). Amendment: [spec.md FR-029–FR-035](../../specs/001-privacy-account-lifecycle/spec.md#functional-requirements), drafted 2026-09-25, awaiting approval.

## Change control

Any new field, purpose, supplier or log source needs this record updated
first, together with the notice, `suppliers.md` and `retention.md`
([operations.md](../../specs/001-privacy-account-lifecycle/contracts/operations.md#maintained-artifacts)).
Keep superseded versions in Git history. Don't rewrite a signed decision;
add a new dated one.
