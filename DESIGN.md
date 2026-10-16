---
name: Gym Notebook
description: A paper gym log in hairlines, ink and one gold margin rule.
colors:
  margin-gold: "#b68235"
  margin-gold-ink: "#7d5411"
  ledger-ink: "#201f1d"
  pale-paper: "#f3f2f2"
  paper-shade: "#eae9e9"
  pencil-grey: "#605d5d"
  dashed-grey: "#7d7979"
  tab-edge: "#9b9797"
  tab-fill: "#d7d3d3"
  brick: "#9b3a2a"
  lamp-black-paper: "#191715"
  lamp-black-shade: "#211f1d"
  night-ink: "#e1ddd8"
  night-gold-ink: "#cc9a55"
  night-brick: "#d9826d"
typography:
  display:
    fontFamily: "Cormorant Garamond, Georgia, Times New Roman, serif"
    fontSize: "52px"
    fontWeight: 400
    lineHeight: 1.04
  headline:
    fontFamily: "Cormorant Garamond, Georgia, Times New Roman, serif"
    fontSize: "42px"
    fontWeight: 600
    lineHeight: 1.12
    letterSpacing: "-0.015em"
  title:
    fontFamily: "Cormorant Garamond, Georgia, Times New Roman, serif"
    fontSize: "22px"
    fontWeight: 600
    lineHeight: 1.12
    letterSpacing: "-0.015em"
  body:
    fontFamily: "Lora, Georgia, Times New Roman, serif"
    fontSize: "15px"
    fontWeight: 400
    lineHeight: 1.55
  input:
    fontFamily: "Lora, Georgia, Times New Roman, serif"
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.55
  label:
    fontFamily: "Lora, Georgia, Times New Roman, serif"
    fontSize: "12px"
    fontWeight: 400
    letterSpacing: "0.08em"
  kicker:
    fontFamily: "Lora, Georgia, Times New Roman, serif"
    fontSize: "11px"
    fontWeight: 400
    letterSpacing: "0.28em"
  button:
    fontFamily: "Cormorant Garamond, Georgia, Times New Roman, serif"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0.04em"
rounded:
  sm: "2px"
  md: "4px"
  round: "50%"
spacing:
  "1": "4.6px"
  "2": "9.2px"
  "3": "13.8px"
  "4": "18.4px"
  "6": "27.6px"
  "8": "36.8px"
  gutter: "22px"
  hit-target: "44px"
  page-max-width: "430px"
components:
  button-primary:
    backgroundColor: "transparent"
    textColor: "{colors.margin-gold-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.md}"
    height: "48px"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.ledger-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.md}"
    height: "44px"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.margin-gold-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.md}"
    height: "44px"
  input:
    backgroundColor: "transparent"
    textColor: "{colors.ledger-ink}"
    typography: "{typography.input}"
    rounded: "{rounded.md}"
    padding: "6px 12px"
    height: "44px"
  warm-up-tag:
    backgroundColor: "transparent"
    textColor: "{colors.pencil-grey}"
    rounded: "{rounded.md}"
    height: "44px"
  warm-up-tag-pressed:
    backgroundColor: "{colors.tab-fill}"
    textColor: "{colors.ledger-ink}"
    rounded: "{rounded.md}"
    height: "44px"
  set-done:
    backgroundColor: "transparent"
    textColor: "{colors.ledger-ink}"
    rounded: "{rounded.round}"
    size: "44px"
  set-done-pressed:
    backgroundColor: "{colors.ledger-ink}"
    textColor: "{colors.pale-paper}"
    rounded: "{rounded.round}"
    size: "44px"
---

# Design System: Gym Notebook

## Overview

**Creative North Star: "The Ruled Page"**

Gym Notebook looks like a well-kept page of a paper log, not a picture of a notebook. There are no spiral bindings, paper textures or handwriting fonts. The page is suggested by what paper actually has: a pale ground, dark serif ink, hairline rules dividing one entry from the next, and a single gold line in the margin. Structure comes from rules and whitespace, never from cards, panels or filled blocks.

The interface is quiet so the figures can lead. Weights, reps, dates and times are set in tabular figures and given the reading weight. Controls are drawn rather than built: outlines and strokes that gain weight, not fill, when they matter more. The one place the system goes solid is the state a lifter must read at arm's length, a done set's filled ink disc.

Density is phone-first and one-handed: a single 430px column, generous vertical rhythm, and every tappable thing a 44px target. At night the same page is printed on lamp-black paper; nothing else about it changes.

**Key Characteristics:**
- Editorial serif pairing: Cormorant Garamond headings over Lora body.
- Hairline rules instead of boxes; a flat page with shadows only on what floats.
- One accent, gold, used as stroke and rule; never a fill, never on its own as text.
- One primary action per screen, ranked by stroke weight, not by colour block.
- Status carried by shape (filled, hollow, dashed, solid) and a word, never by colour alone.
- Dark mode follows the OS, with each tonal ramp mirrored so a step keeps its role.

## Colors

A near-monochrome paper-and-ink palette with one warm metal accent and one reserved error colour.

### Primary
- **Margin Gold** (#b68235): decoration only. Rules under headings, the cover's ornament and inner frame, the chart's line and points, the hover tint behind primary and ghost buttons, the selection wash. At about 3:1 on the paper it is too faint to read as words.
- **Margin Gold Ink** (#7d5411, the 700 step of the gold ramp): the gold that carries meaning. Links, primary and ghost button labels and strokes, focus rings, the caret, and the "next set" ring.

### Neutral
- **Ledger Ink** (#201f1d): all body text and headings, the filled Done disc, the current section in the notebook nav. Messages that are news rather than failure ("Page saved") stay in ink.
- **Pale Paper** (#f3f2f2): the ground every screen sits on.
- **Paper Shade** (#eae9e9): the surface one step off the ground, for floating layers such as the template chooser and the save-as-template band.
- **Pencil Grey** (#605d5d, neutral 700): every secondary line, including kickers, field labels, meta lines, set numbers, chart ticks and placeholders. Never lighter than this step for text.
- **Dashed Grey** (#7d7979, neutral 600): the dashed border of a planned, not-yet-lifted set's fields.
- **Tab Edge / Tab Fill** (#9b9797 / #d7d3d3, neutral 500 / 300): the border and fill of a pressed Warm-up tag.
- **Hairline**: ink at 16% (`color-mix` of Ledger Ink with transparent). Every divider, field border, secondary button outline and the cover's outer frame.

### Error
- **Brick** (#9b3a2a): failures only. An invalid field's border and a form's error line. Nothing that is not a failure uses it, so an error is never mistaken for a link or a gain.

### Night
Dark mode redefines only colour tokens: **Lamp-black Paper** (#191715) ground, **Lamp-black Shade** (#211f1d) surface (a step *lighter*, which is how raised reads on a dark ground), **Night Ink** (#e1ddd8) text, **Night Gold Ink** (#cc9a55) for gold labels, **Night Brick** (#d9826d) for errors. The 100–900 ramps are mirrored in lightness, so neutral 700 is still the secondary text and accent 700 still the readable gold.

### Named Rules
**The Stroke-Only Gold Rule.** Gold is a line, never a filled block and never text by itself. When gold must say something, it is Margin Gold Ink.

**The One Brick Rule.** Brick means something failed. Status, success and saved states are ink.

## Typography

**Display Font:** Cormorant Garamond (with Georgia, Times New Roman)
**Body Font:** Lora (with Georgia, Times New Roman)

**Character:** A high-contrast book face for names and headings over a sturdy text serif for everything read in running lines. Both are self-hosted variable fonts, so nothing is fetched from a third party.

### Hierarchy
- **Display** (400, 52px, 1.04): the cover title only. The display cut is the normal weight, lighter than interface headings.
- **Headline** (600, 42px, 1.12, −0.015em): the base `h1`; screen headers often set it smaller (27px on Sessions). `h2` 32px, `h3` 25px share the cut.
- **Title** (600, 22px): an exercise block's name on the session editor; the cover owner's name sits at 24px.
- **Body** (400, 15px, 1.55): all prose and set rows. Inputs are 16px so iOS Safari never zooms on focus.
- **Label** (400, 12px, 0.08em, uppercase): field labels and set-column heads, in Pencil Grey.
- **Kicker** (400, 11px, 0.28em, uppercase): the small-capitals line above a title ("Training log"). 11px is the floor; nothing is smaller.
- **Button** (Cormorant 600, 16px, 0.04em): button labels take the heading face; the primary steps up to 17px.

### Named Rules
**The Tabular Figures Rule.** Wherever numbers stand as figures (weights, reps, times, dates, chart axes), set them tabular so columns line up.

**The 11px Floor Rule.** No text below 11px; the most-tapped control, Done, uses 13px.

## Layout

A single centred column, max 430px wide, with a 27.6px side padding on ordinary screens and a 22px inner gutter on the full-bleed list screens (Sessions, editor). The reference phone is 390 × 844; desktop is the same column, centred, with no separate layout. Full-height screens size to `--app-height` (100dvh minus the safe-area insets), and the body pads itself clear of the notch and home indicator when run from the home screen.

Spacing steps by 4.6px (4.6, 9.2, 13.8, 18.4, 27.6, 36.8). Entries are separated by a hairline and around 16px of padding rather than by gaps between boxes. Lists scroll inside the column with room left at the bottom for the action footer.

The set row is a fixed grid: number, weight, reps, Warm-up, then × or Done, with tracks sized independently of content so headers align with inputs. Narrow phones (360–370px and below) tighten tracks rather than wrapping.

### Named Rules
**The Thumb Rule.** Every tappable thing, text links in headers included, has a box of at least 44px. The type can stay small; the box grows.

## Elevation & Depth

The page is flat. Depth is made by hairlines and tonal steps, and shadows appear only on layers that float above the page's flow: the exercise autocomplete list (`--shadow-sm`) and the "start a session from" template chooser (`--shadow-md`). Shadows are built from a dedicated shadow colour (ink by day, black at higher strength by night), never from the text colour, so they never glow in dark mode.

### Shadow Vocabulary
- **Lifted list** (`box-shadow: 0 1px 2px color-mix(in srgb, #2d2b2b 14%, transparent)`): autocomplete suggestions under a field.
- **Floating chooser** (`box-shadow: 0 3px 10px color-mix(in srgb, #2d2b2b 16%, transparent)`): an inline chooser standing over the list, not a modal.

### Named Rules
**The Flat Page Rule.** If it is part of the page, it has a rule, not a shadow. Only something laid over the page casts one.

## Shapes

Corners are barely softened: 4px on buttons, fields, tags and floating layers, 2px on the smallest marks. The one exception is the cover, whose two nested frames (an ink hairline at 22px radius, a gold one at 18px inset inside it) make the screen read as a closed book. Circles are kept for the Done toggle, so a round control always means "this set". Borders are 1px hairlines; weight (2px) is reserved for primacy (the primary button), state (a done disc, the next set's ring) and errors (an invalid field's doubled border).

## Components

### Buttons
Drawn, not built: every button is transparent with a stroke or none.
- **Shape:** gently squared corners (4px), at least 44px tall, Cormorant 600 label.
- **Primary:** a 2px Margin Gold Ink stroke and label, 17px type, 48px tall. One per screen, plus once inside an inline confirmation that is the screen's current question.
- **Secondary:** a hairline outline, ink label.
- **Ghost:** no outline, Margin Gold Ink label, 8px inline padding; reads as a text link with a full target.
- **Hover / Active:** a translucent wash, gold at 12% / 22% for primary, ink at 7% / 14% for secondary, gold at 10% / 18% for ghost.
- **Focus:** a 2px Margin Gold Ink outline, 2px offset.
- **Disabled:** 45% opacity.

### Inputs / Fields
- **Style:** transparent, hairline border, 4px radius, 44px tall, 16px text, Margin Gold Ink caret. Labels sit above in the Label style.
- **Hover:** the border darkens to ink at 45%.
- **Focus:** the border turns Margin Gold Ink; no outer glow.
- **Error:** Brick border doubled to 2px by an inset shadow, so invalid differs from focused without colour. Focused while invalid, it keeps the brick border and gains the gold focus ring outside it.
- **Planned (not yet lifted):** fields go dashed in Dashed Grey.
- **Search clear:** WebKit's blue circle is redrawn as a plain Pencil Grey cross, so the screen has one kind of ×.

### Navigation
The notebook nav is a row of words under each screen's title, the same sections in the same order everywhere, at 13px. The section in view is ink at 600 and not a link; the others are Margin Gold Ink links with 44px boxes. "Cover" sits apart at the row's right edge as the way out of the notebook. Header back links ("← Sessions") use the same 44px text-link box.

### Lists
Session rows are a two-column grid: a fixed 56px date column and the title with its meta line, separated by hairlines with 16px vertical padding. Hover adds a 4% ink wash; focus is the gold ring at 3px offset. Two sessions on one date are two rows, always.

### Notices
An arrival notice ("Page saved", "Merged into Bench Press") is a ruled strip under the header: a line written on the page, not a toast laid over it. Form messages sit under the form as prose, Brick for failures and ink for news.

### Set Status Controls (signature)
- **Warm-up tag:** a hairline rectangle with the word "Warm-up" in Pencil Grey. Pressed, it fills with Tab Fill and gains a Tab Edge border with ink text. A warm-up row's figures recede to Pencil Grey.
- **Done toggle:** a 44px circle. Not done: a hollow Pencil Grey ring reading "Done". Done: a filled Ledger Ink disc with a check in paper. The set the lifter is up to has a 2px Margin Gold Ink ring and its number in ink at 600.
- **Skipped:** the toggle goes dashed and a "skipped" note sits under the row.
- The row's × never takes Done's place, so the control tapped after every set is never destructive.

### Progress Chart
Hand-drawn SVG: hairline gridlines, a 1.5px Margin Gold polyline with round joins and stroked points. Axis labels are HTML in the body face, not SVG text, positioned against a 38px left gutter. The chart always states its metric and unit.

### Cover (signature)
A closed book: two nested rounded frames, the display title, a gold ornament (two short rules either side of a small outlined diamond), the owner's name in Cormorant and a small-capitals volume line. Account actions sit below a hairline in a quieter two-column group.

### Motion
Motion is nearly absent. The one deliberate moment is on Backup: after a full backup, a gold rule draws under the new date (1.1s, `cubic-bezier(0.16, 1, 0.3, 1)`) and settles to a hairline; with reduced motion it simply appears.

## Do's and Don'ts

### Do:
- **Do** separate entries with 1px hairlines (ink at 16%) and whitespace, not boxes.
- **Do** use Margin Gold Ink (#7d5411) for any gold that is read: links, button labels, focus rings.
- **Do** rank actions by stroke weight: one 2px primary per screen, hairline secondary, ghost as text.
- **Do** keep every target at least 44px, including header text links.
- **Do** set figures tabular and keep secondary text at Pencil Grey (neutral 700), never 600 or lighter.
- **Do** carry set status by shape and word (filled, hollow, dashed, "skipped") so it holds without colour and in both themes.
- **Do** change only colour tokens for dark mode; mirror ramps so steps keep their role.

### Don't:
- **Don't** fill a block with gold or set text in plain Margin Gold (#b68235).
- **Don't** use Brick for anything but failures; saved and sent states are ink.
- **Don't** add shadows to anything that sits in the page's flow; only floating layers cast them.
- **Don't** add paper textures, spiral bindings, handwriting fonts or other skeuomorphic notebook props: the page is editorial, not a costume.
- **Don't** use toasts; notices are ruled lines written on the page.
- **Don't** set any text below 11px, or input text below 16px.
- **Don't** add streak, badge or celebration visuals; the page is a record, not a motivation engine.
- **Don't** hard-code hex values in component CSS; reference the tokens in `frontend/src/styles/tokens.css`.
