# T043 privacy notice — owner review draft

**Status:** Draft for controller review, updated 2026-09-29. This file and
[the matching JSON candidate](notice-candidate.json) are outside `notices/`
and are not embedded or served by the API. P1, P2 and P5 have signed decisions.
The owner approved five wording sections on 2026-09-29, as recorded below;
the other conclusions and public wording still require final review.
Retention and supplier claims require the checks in
[release-checklist.md](release-checklist.md). Do not publish this text or mark
T043 complete until the decisions and evidence below are recorded.

**Contact address:** The owner supplied `jouni.uu@proton.me` on 2026-09-28.
This spelling already appears in the draft and related privacy records.
On 2026-09-28 the owner reported that this address received a test message
sent from a Gmail account and that a reply sent from this address worked. The
report date is the recorded test evidence; no mailbox content is kept in Git.
The owner confirmed on 2026-09-28 that they will check the mailbox every working day,
including while away; no other person needs mailbox access. This is an
operating commitment to monitor the address.

**Public identity:** The owner confirmed on 2026-09-28 that the notice may
name them as "Jouni Uusimaa, a private individual in Finland."

## Registration service description

Wording and placement beside **Create account** approved by the owner on
2026-09-28. The copy is in the frontend and UI prototype, and was confirmed in
the production bundle (`main` at `2362352`) on 2026-09-28.

> Gym Notebook is a free, invite-only training log provided by Jouni
> Uusimaa. Creating an account asks us to keep your private record of workout
> dates, exercises and sets, and show your progress chart. A username and
> password let you return to that notebook.

This describes the requested core service for the Article 6(1)(b) decision in
[processing-decision.md](processing-decision.md), where P1 and P2 were signed on
2026-09-28.
The privacy notice and optional-details consent remain separate; this copy
adds no consent checkbox. The login page already links to the notice when the
privacy feature is enabled.

## Proposed public wording

**Partial wording approval (owner, 2026-09-29, conversation):** the owner
approved the text in this draft and the matching JSON candidate at commit
`7254cbc` for “About this notice,” “Who is responsible,” “Information in your
account and notebook,” “Your choices and rights,” and “Contact and complaints.”
This approval is limited to those five sections. The purposes, recipients and
retention sections, the complete notice version, and publication remain open.

### About this notice

Gym Notebook is a private training log. This notice explains what happens to
information you give the service and how to exercise your privacy rights.
Reading this notice or pressing Continue records only that you saw this
version. It is not consent to optional workout details.

### Who is responsible

Jouni Uusimaa, a private individual in Finland, is the controller of Gym
Notebook. For privacy questions and requests, email jouni.uu@proton.me.

### Information in your account and notebook

To create and use an account, you provide a username and password. The
service stores a password hash, not the password itself, plus account creation,
session-control and privacy-notice acknowledgement records. Without a username
and password, the service cannot maintain a private notebook for you. An
invite code is checked at registration but is not stored with your account.

Your notebook contains workout dates and times, exercise names and whether an
exercise is bodyweight based, sets, weights, repetitions and warm-up marks.
The service calculates a progress chart from your sets when you ask to see it;
the chart values are not kept separately. Exercise names are needed to record
sets. Please use lift names rather than health details in exercise names.

A workout's title, location, notes and your bodyweight are optional. They can
reveal health information. New details are stored only if you choose Allow on
a separate consent screen. If you choose Not now, the four fields stay hidden
and the rest of the notebook works. You can allow them later or withdraw from
Privacy & account. Withdrawing removes those four fields from all your
workouts; the other notebook information stays. If your account already held
these details when this choice was introduced, you are asked whether to keep
them. Choosing Not now removes them; if you do not answer, they are cleared
30 calendar days after this feature is enabled. Silence is not consent.

The browser stores your sign-in token on your device so you can stay signed
in. The service also processes limited operational and security information,
including error and deletion-recovery records. If you email the privacy
contact, your message and minimal case-handling records are processed to
answer your request. Please do not send your password or sign-in token by
email.

### Why the information is used

Account and core notebook information are used to provide your private
training log and progress chart. The lawful basis is GDPR Article 6(1)(b),
performance of the service you request. Notice acknowledgement is used to
document which version you saw; the basis for that record is Article 6(1)(c),
compliance with transparency obligations.

Optional workout details are used only to show them in your own notebook and
include them in your export. The Article 6 basis is your consent under Article
6(1)(a). Because these fields can contain health information, they also
require your explicit consent under Article 9(2)(a). You may withdraw that consent at any
time without affecting earlier lawful processing or use of the rest of the
notebook. Consent to these details is separate from pressing Continue on this
notice.

Limited operational and security records are used to run and protect the
service. The basis is Article 6(1)(f), the controller's legitimate
interest in a working and secure service. Minimal deletion-recovery records
help prevent a deleted account from reappearing after a database restore; the
basis is Article 6(1)(c), compliance with erasure obligations. The privacy
contact uses correspondence and a minimal request register to handle your
rights request and document the response; the basis is Article 6(1)(c). The
sign-in token in your browser supports the requested signed-in service; its
GDPR basis is Article 6(1)(b).

Gym Notebook does not make automated decisions about you or profile you. It
does not use your notebook for advertising or analytics.

### Who receives information and where

Microsoft Azure runs the application and holds its operational logs in Sweden.
Neon hosts the notebook database in Frankfurt, Germany. Cloudflare currently
provides DNS for the site; it is not the site's HTTP proxy. Proton Mail
receives messages sent to the privacy contact. The controller also keeps a
minimal rights-request register on a restricted local device in Finland. A
downloaded notebook export is saved to the device you choose.

These providers may use their own supporting services or remote support
access. Proton states that encrypted mail storage may be in Switzerland,
Germany or Norway. Switzerland has an EU adequacy decision.

### How long information is kept

Account and notebook information remains in the active database until you
delete the account. Withdrawing optional-details consent removes those
details from the active notebook immediately. Deleting the account removes
its active account, notebook and consent/acknowledgement records. Information
in recovery copies may remain for no more than 30 calendar days from the
deletion. It is restricted to recovery use, and a restore must remove deleted
accounts before access resumes.

Identifying operational and security logs have an intended maximum of 30
calendar days from the original log entry. After deletion, only minimal logs
with a documented reason to keep them may remain until their original expiry;
the deadline is not restarted by deletion. Minimal deletion-recovery evidence
has an additional ceiling of 31 calendar days from the deletion, whichever
deadline comes first. No notebook content or credentials are intended for
these logs.

The sign-in token permits requests for up to 30 minutes after issue. The
browser removes its stored copy on sign-out or when it observes an invalid
session; an offline browser may retain the bytes until it is next used.
The service does not keep a completed server-side export copy. Routine
rights-request correspondence and the minimal register are scheduled for
deletion no later than 12 calendar months after the case closes. A specific
legal obligation or dispute may require a documented, limited exception.

### Your choices and rights

You can view and edit your notebook, download a JSON copy from Privacy &
account, and delete your account there. For access to other information we
may hold about you, or to ask for correction, erasure, restriction or
portability where applicable, email jouni.uu@proton.me. You may object to
processing based on legitimate interests. You may also withdraw the separate
consent for optional workout details from Privacy & account.

We will respond without undue delay and within one calendar month of receiving
a rights request, or explain a permitted extension within that month. We may
need a proportionate identity check before disclosing or changing information.
You do not need to send a password by email.

### Contact and complaints

Contact Jouni Uusimaa at jouni.uu@proton.me for privacy questions or rights
requests. You may complain to Finland's Office of the Data Protection
Ombudsman (Tietosuojavaltuutetun toimisto) at
https://tietosuoja.fi/en/home. You do not need to contact Gym Notebook before
making a complaint.

## Controller decisions and evidence required before publication

| Notice item | Current review state |
| --- | --- |
| Controller, contact, Finnish authority | Name and address confirmed by owner; receipt, reply and personal workday monitoring reported. Authority identified in the processing decision. |
| Required/optional information, consent distinction, rights, profiling | Owner approved the about, controller, information, rights and contact sections on 2026-09-29; the purposes section, including the profiling statement in its context, still awaits final review. |
| Purposes and lawful bases | Article 6(1)(a) plus Article 9(2)(a) chosen for optional details; FR-029–FR-035 and the existing-value transition approved 2026-09-29 (T042). P1, P2 and P5 signed 2026-09-28. P4 bases chosen but unsigned pending log findings; P7 basis recorded, with the Article 9 point for volunteered health details open. |
| Recipients, locations and transfer safeguards | Current supplier inventory is a draft; T076 must settle account-specific agreements, support paths and transfer safeguards. |
| Retention and restored copies | Draft reflects the schedule's intended limits; T075–T077 must verify actual content, copies and disposal before these claims are published. |
| Version and effective date | `notice-candidate.json` has no effective or publication date and is not served. Create the reviewed version and index entry only after content and release evidence are settled. |

The proposed public wording above presents legal bases as final because that
is how a published notice must read. The owner chose Article 6(1)(a) consent
for the four optional workout details on 2026-09-28, alongside Article 9(2)(a)
explicit consent. The owner also signed P1 and P2 on 2026-09-28: Article 6(1)(b)
for account and core notebook processing, Article 6(1)(c) for notice
acknowledgement and deletion-recovery records, and P2's core notebook
information is not health data in this context, so it needs no Article 9
condition. These choices are
recorded in the [processing decision](processing-decision.md). The remaining
legal bases are under review. The [EDPB's Article 6(1)(b)
guidance](https://www.edpb.europa.eu/sites/default/files/files/file1/edpb_guidelines-art_6-1-b-adopted_after_public_consultation_en.pdf)
informs the contract-necessity assessment.

The remaining purpose decisions to review are:

| Record | Proposed conclusion to confirm or revise |
| --- | --- |
| P4 operational logs | Owner chose Article 6(1)(f) on 2026-09-28; the balance still needs actual log content, access and retention from T075–T077. The credential found in old startup lines was rotated; those lines still need observed expiry. |
| P4 deletion evidence | Owner chose Article 6(1)(c) on 2026-09-28; signed with P4 once T075–T077 evidence exists. |
| Rights correspondence/register | Owner chose Article 6(1)(c) on 2026-09-28 (P7); disposal and recipient handling still need T076/T083 evidence. |

1. Sign P4 in [processing-decision.md](processing-decision.md) once its
   T075–T077 findings are resolved. (P1, P2 and P5 were signed on 2026-09-28,
   T042 was approved on 2026-09-29, P7 recorded the request-correspondence basis, and the registration service
   description is live. The notice wording above already matches these bases.)
2. The owner has confirmed the public name, reported successful receipt from
   Gmail and an outgoing reply, and committed to checking the contact mailbox
   each working day, including while away. These owner reports are recorded
   above. Follow the [rights-request procedure](rights-requests.md); review
   the coverage arrangement if personal checks stop being possible. Do not
   put test messages or mailbox screenshots in Git.
3. Finish supplier agreement, transfer, support-access and deletion-assistance
   checks (T076), plus log-content and disposal checks (T075–T077). Reconcile
   their results with the recipients and retention sections above. A declared
   maximum is not proof of actual disposal.
4. Reconcile the [JSON candidate](notice-candidate.json) with these findings,
   including any transfers and safeguards, review its final wording as
   controller, record reviewer/date/evidence, and create a new immutable
   version in `notices/` with a reviewed `effectiveAt`,
   `publishedAt`, `materialChangeSummary`, `owner`, `reviewDate` and
   `reviewEvidence`. Update `index.json` to point at it. Keep the synthetic
   versions in Git for development history, but do not list them as published
   history after the first real notice takes effect. Rebuild the API image,
   check the public route and notice acknowledgement, and record the version
   and observed result in [release-checklist.md](release-checklist.md).

Legal content checklist: [GDPR Article 13](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng),
[EDPB transparency guidance](https://www.edpb.europa.eu/system/files/2023-09/wp260rev01_en.pdf),
and the [Finnish authority](https://www.edpb.europa.eu/contact/file-a-complaint_en).
