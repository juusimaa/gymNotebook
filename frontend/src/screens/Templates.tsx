import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { listTemplates, type TemplateSummaryResponse } from '../api/templates'
import { useRouteNotice } from './routeNotice'
import { describeTemplateCounts } from './templateDraft'
import './Sessions.css'
import './Templates.css'

// /templates (specs/006 US4, docs/ui/README.md → 9. Templates): the caller's
// templates by name. A row opens the template editor; New template starts an
// empty one. Laid out like Sessions, whose header, scrolling list and footer
// classes it shares.
export default function Templates() {
  // "Template saved." or "Template deleted. …" from the editor.
  const notice = useRouteNotice()
  const [templates, setTemplates] = useState<TemplateSummaryResponse[] | null>(
    null,
  )
  const [message, setMessage] = useState<string | null>(null)

  // Bumped by Try again: a new value reruns the effect, as on Sessions.
  const [loadAttempt, setLoadAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const response = await listTemplates()
        if (!cancelled) setTemplates(response)
      } catch {
        if (!cancelled) setMessage('Your templates could not be loaded.')
      }
    }

    void load()
    return () => {
      cancelled = true
    }
  }, [loadAttempt])

  function retry() {
    setMessage(null)
    setLoadAttempt((attempt) => attempt + 1)
  }

  return (
    <main className="page sessions templates">
      <header className="sessions-header">
        <h1>Templates</h1>
        <Link className="header-link" to="/workouts">
          ← Sessions
        </Link>
      </header>
      {notice !== null && (
        <p className="form-message is-status route-notice" role="status">
          {notice}
        </p>
      )}

      <div className="sessions-list">
        <p className="templates-explanation">
          A template is a set of exercises and sets to plan a session from.
          Planning from one copies it; the template stays as it is.
        </p>

        {message !== null ? (
          <div className="templates-error">
            <p className="form-message" role="alert">
              {message}
            </p>
            <button className="btn btn-ghost" type="button" onClick={retry}>
              Try again
            </button>
          </div>
        ) : templates === null ? (
          <p className="templates-empty">Opening templates…</p>
        ) : templates.length === 0 ? (
          <p className="templates-empty">
            No templates yet. Save one from a session page or a planned session,
            or write one here.
          </p>
        ) : (
          <ul className="templates-list">
            {templates.map((template) => (
              <li key={template.id}>
                <Link className="template-row" to={`/templates/${template.id}`}>
                  <h2>{template.name}</h2>
                  <p className="session-exercises">
                    {template.exerciseNames.join(' · ')}
                  </p>
                  <p className="session-meta num">
                    {describeTemplateCounts(template)}
                  </p>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>

      <footer className="sessions-action">
        <Link className="btn btn-primary" to="/templates/new">
          New template
        </Link>
      </footer>
    </main>
  )
}
