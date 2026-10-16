import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { ApiError } from '../api/client'
import {
  listTemplates,
  startFromTemplate,
  type TemplateSummaryResponse,
} from '../api/templates'
import { createStartTimes } from './newWorkoutDraft'
import { useRouteNotice } from './routeNotice'
import { describeTemplateCounts } from './templateDraft'
import './Sessions.css'
import './Templates.css'
import { NotebookNav } from './NotebookNav'

// /templates (specs/006 US4, docs/ui/README.md → 9. Templates): the caller's
// templates by name. A row opens the template editor, and Start beside it
// starts a session from the template at once; New template starts an empty
// one. Laid out like Sessions, whose header, scrolling list and footer
// classes it shares.
export default function Templates() {
  // "Template saved." or "Template deleted. …" from the editor.
  const notice = useRouteNotice()
  const [templates, setTemplates] = useState<TemplateSummaryResponse[] | null>(
    null,
  )
  const [message, setMessage] = useState<string | null>(null)
  // The template whose Start is on its way, and what went wrong last time.
  const [startingId, setStartingId] = useState<number | null>(null)
  const [startMessage, setStartMessage] = useState<string | null>(null)
  const navigate = useNavigate()

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

  // Start (specs/006): a new session from the template, dated today and
  // started now, opened in the editor in progress to tick the sets off, as
  // the Sessions chooser does.
  async function startSession(template: TemplateSummaryResponse) {
    if (startingId !== null) return
    setStartingId(template.id)
    setStartMessage(null)
    const { date, startedAt } = createStartTimes(new Date())
    try {
      const session = await startFromTemplate(template.id, date, startedAt)
      void navigate(`/workouts/${session.id}/edit`)
    } catch (error: unknown) {
      setStartingId(null)
      if (error instanceof ApiError && error.status === 404) {
        // Deleted on another device: reload the list, which is out of date.
        setStartMessage(`${template.name} no longer exists.`)
        setLoadAttempt((attempt) => attempt + 1)
      } else {
        setStartMessage('The session could not be started. Please try again.')
      }
    }
  }

  return (
    <main className="page sessions templates">
      <header className="sessions-header">
        <h1>Templates</h1>
        <NotebookNav current="templates" />
      </header>
      {notice !== null && (
        <p className="form-message is-status route-notice" role="status">
          {notice}
        </p>
      )}

      <div className="sessions-list">
        <p className="templates-explanation">
          A template is a set of exercises and sets to start a session from.
          Starting one copies it; the template stays as it is.
        </p>
        {startMessage !== null && (
          <p className="form-message templates-start-message" role="alert">
            {startMessage}
          </p>
        )}

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
            No templates yet. Save one from a session page, or write one here.
          </p>
        ) : (
          <ul className="templates-list">
            {templates.map((template) => (
              // The row opens the editor; Start sits beside it, never inside
              // it, as Continue logging does on Sessions.
              <li className="template-entry" key={template.id}>
                <Link className="template-row" to={`/templates/${template.id}`}>
                  <h2>{template.name}</h2>
                  <p className="session-exercises">
                    {template.exerciseNames.join(' · ')}
                  </p>
                  <p className="session-meta num">
                    {describeTemplateCounts(template)}
                  </p>
                </Link>
                <button
                  className="btn btn-secondary template-start"
                  type="button"
                  aria-label={`Start a session from ${template.name}`}
                  disabled={startingId !== null}
                  aria-busy={startingId === template.id}
                  onClick={() => void startSession(template)}
                >
                  {startingId === template.id ? 'Starting…' : 'Start'}
                </button>
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
