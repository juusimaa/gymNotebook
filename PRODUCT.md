# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Lifters in a small but growing group. Signup has been open since milestone 12
(email confirmation, sending caps and a bot check replaced the invite code).
The author is the first user, but the product is meant to serve the wider
group over time, so their needs carry real weight in decisions.

They use it in two situations:

- **At the rack, mid-session:** standing, often one-handed, on a phone,
  logging sets between efforts. Speed and large targets matter more than
  anything else here.
- **Afterwards, reviewing:** reading back a past session, or checking whether
  a lift is progressing on the e1RM chart.

## Product Purpose

A digital replacement for a paper gym log: workouts → exercises → sets, with a
progress chart of estimated one-rep max. Success means a lifter stops carrying
the paper notebook because this log is as quick to write in and more useful to
read back.

It is also the author's C# / ASP.NET Core learning project (see
[AGENTS.md](AGENTS.md) and [PLAN.md](PLAN.md)). That shapes how it is built, but
not what it is for.

## Positioning

A faithful paper log, not a fitness platform. Where Strong or Hevy add social
feeds, streaks and gamification, Gym Notebook keeps the notebook's structure
and its honesty:

- a page is one session, not one day — two sessions on a Tuesday are two pages;
- warm-up sets are written down but demoted, and never count toward progress;
- the progress figure follows explicit rules (a tested single is the weight
  itself, bodyweight exercises chart reps, Epley is flagged as unreliable above
  roughly 12 reps) and states its metric and unit on screen.

## Operating Context

- A session page has a heading (date, start/end time, optional title,
  bodyweight, gym, notes) followed by exercise blocks in order, each with its
  sets.
- Exercises are per-user free text, added by being typed into autocomplete;
  there is no "create exercise" action. Typos are fixed by rename, and renaming
  onto an existing name merges the two.
- Privacy and account lifecycle (notice gate, export, account deletion,
  consent for optional workout details) is a live feature behind
  `PRIVACY_LIFECYCLE_ENABLED`; see
  [specs/001-privacy-account-lifecycle/](specs/001-privacy-account-lifecycle/).

## Capabilities and Constraints

- Screens: login, cover, sessions list, session page, new page, progress,
  exercise index, edit exercise, plus the privacy and account screens. The spec
  is [docs/ui/README.md](docs/ui/README.md), and its "Rules the UI must not
  break" list is binding.
- Phone-first single column (390 × 844 reference). Desktop is the same column,
  centred.
- kg only; bodyweight to one decimal; plates in 1.25 kg steps.
- Interface language is English; times use Finland's 24-hour `HH.mm`
  convention (`07.15`).
- Hand-written CSS against tokens in `frontend/src/styles/`; no component
  library, by decision.
- Terminology: *page* / *session* (one `Workout`), *block* (one exercise's
  appearance in a session), *set*, *warm-up*, *e1RM*, *tested single*,
  *bodyweight* exercise, *added weight*.
- Still open: empty states, error/offline states, and date-range filtering on
  the progress view (from docs/ui "Still open").

## Brand Commitments

- Name: **Gym Notebook**.
- Notebook vocabulary is used throughout the copy: "Open the notebook",
  "Start a new page", "Tear out this page?". The cover is deliberately a
  closed book, not a dashboard.
- Copy explains consequences plainly: destructive actions name what they
  destroy, and absent optional fields read as prose ("bodyweight not logged")
  rather than empty slots.
- An established visual direction already exists in docs/ui and the
  [prototype](docs/ui/prototype.html). It is recorded there, not here.

## Evidence on Hand

- Clickable prototype with sample data: [docs/ui/prototype.html](docs/ui/prototype.html),
  including a same-date session pair and a typo'd "Bnech Press" to show merging.
- No testimonials, user counts, benchmarks or press exist, and none should be
  invented.

## Product Principles

1. **The notebook's structure is the model.** Session → exercise → set; never
   merge or group pages by date.
2. **Honest numbers.** Every figure says what it is and in what unit, and the
   UI never shows an estimate where a measurement exists.
3. **Fast at the rack.** Logging a set comes first: one hand, large targets,
   few steps.
4. **Nothing is lost by accident.** Destructive actions name their
   consequences. Exercises with history are renamed or merged, never deleted.
5. **Calm, not gamified.** No streaks, badges or social layer. The log is a
   record, not a motivation engine.

## Accessibility & Inclusion

- Hit targets of at least 44px, for one-handed use at a rack.
- Keyboard and mobile use are explicit acceptance criteria for the privacy
  screens (owner walkthrough, spec 001). Focus moves to the heading on screen
  load, and status lines are polite live regions.
- No formal WCAG conformance level has been committed to.
