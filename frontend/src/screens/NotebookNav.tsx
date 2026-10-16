import { Link } from 'react-router'

// The notebook's sections, in one order on every screen that lists them, so a
// link is always where it was last time (design critique 2026-10-16: each
// screen used to carry its own subset, and Templates was reachable only from
// the template chooser).
const SECTIONS = [
  { key: 'sessions', label: 'Sessions', to: '/workouts' },
  { key: 'progress', label: 'Progress', to: '/progress' },
  { key: 'exercises', label: 'Exercises', to: '/exercises' },
  { key: 'templates', label: 'Templates', to: '/templates' },
] as const

export type NotebookSection = (typeof SECTIONS)[number]['key']

// The current section stays in the row as plain ink with aria-current, rather
// than vanishing, so the row never reflows between screens. The cover closes
// the row: it is the way out of the notebook, not a section of it.
export function NotebookNav({ current }: { current: NotebookSection }) {
  return (
    <nav className="notebook-nav" aria-label="Notebook">
      {SECTIONS.map((section) =>
        section.key === current ? (
          <span
            key={section.key}
            className="header-link notebook-nav-current"
            aria-current="page"
          >
            {section.label}
          </span>
        ) : (
          <Link key={section.key} className="header-link" to={section.to}>
            {section.label}
          </Link>
        ),
      )}
      <Link className="header-link notebook-nav-cover" to="/">
        Cover
      </Link>
    </nav>
  )
}
