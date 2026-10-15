import { useEffect, useId, useRef, useState } from 'react'
import { ApiError } from '../api/client'
import { listTemplates, TEMPLATE_NAME_TAKEN } from '../api/templates'
import {
  describeTemplateNameTaken,
  findTakenTemplateName,
} from './templateDraft'
import './SaveAsTemplateBand.css'

interface SaveAsTemplateBandProps {
  // The page title, or '' without one: the name the field starts with.
  initialName: string
  // Writes the template. A thrown ApiError is the band's to explain: a 409
  // template_name_taken at the field, anything else on one line.
  onSave: (name: string) => Promise<void>
  // Cancel or Close. The screen hides the band and puts focus back on the
  // button that opened it.
  onClose: () => void
}

// Save as template (specs/006 US4, docs/ui/README.md → session page): an
// inline band under the header, not a modal. It asks only for a name; what
// gets copied is the screen's business, through onSave.
export function SaveAsTemplateBand({
  initialName,
  onSave,
  onClose,
}: SaveAsTemplateBandProps) {
  const [name, setName] = useState(initialName)
  const [nameMessage, setNameMessage] = useState<string | null>(null)
  const [saveMessage, setSaveMessage] = useState<string | null>(null)
  const [isSaving, setIsSaving] = useState(false)
  // The name as saved, once it is: the band then says so and offers Close.
  const [savedName, setSavedName] = useState<string | null>(null)
  const fieldId = useId()
  const nameErrorId = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)

  // Opening the band puts the cursor in the name; a save moves focus to
  // Close, the only thing left to do, so the status line is heard with it.
  useEffect(() => {
    if (savedName === null) inputRef.current?.focus()
    else closeRef.current?.focus()
  }, [savedName])

  async function save() {
    if (isSaving) return
    const trimmed = name.trim()
    if (trimmed === '') {
      setNameMessage('Name the template.')
      inputRef.current?.focus()
      return
    }

    setIsSaving(true)
    setNameMessage(null)
    setSaveMessage(null)
    try {
      await onSave(trimmed)
      setIsSaving(false)
      setSavedName(trimmed)
    } catch (error: unknown) {
      setIsSaving(false)
      if (
        error instanceof ApiError &&
        error.status === 409 &&
        error.code === TEMPLATE_NAME_TAKEN
      ) {
        // As in the template editor: the message names the other template
        // as it is spelt, and the typed name is kept for correcting.
        const existing = await listTemplates().catch(() => [])
        setNameMessage(
          describeTemplateNameTaken(
            findTakenTemplateName(trimmed, existing, null),
          ),
        )
        inputRef.current?.focus()
      } else {
        setSaveMessage('The template could not be saved. Please try again.')
      }
    }
  }

  if (savedName !== null) {
    return (
      <section className="template-band" aria-label="Save as template">
        <p className="template-band-text" role="status">
          Saved as template <em>{savedName}</em>.
        </p>
        <div className="template-band-actions">
          <button
            ref={closeRef}
            className="btn btn-secondary"
            type="button"
            onClick={onClose}
          >
            Close
          </button>
        </div>
      </section>
    )
  }

  return (
    <section className="template-band" aria-label="Save as template">
      <form
        onSubmit={(event) => {
          event.preventDefault()
          void save()
        }}
      >
        <div className="field">
          <label className="label" htmlFor={fieldId}>
            Template name
          </label>
          <input
            ref={inputRef}
            className="input"
            id={fieldId}
            type="text"
            autoComplete="off"
            value={name}
            disabled={isSaving}
            aria-invalid={nameMessage !== null || undefined}
            aria-describedby={nameMessage === null ? undefined : nameErrorId}
            onChange={(event) => {
              setNameMessage(null)
              setName(event.target.value)
            }}
          />
          {nameMessage !== null && (
            <p id={nameErrorId} className="form-message" role="alert">
              {nameMessage}
            </p>
          )}
        </div>
        {saveMessage !== null && (
          <p className="form-message" role="alert">
            {saveMessage}
          </p>
        )}
        <div className="template-band-actions">
          <button
            className="btn btn-ghost"
            type="button"
            disabled={isSaving}
            onClick={onClose}
          >
            Cancel
          </button>
          <button
            className="btn btn-secondary"
            type="submit"
            disabled={isSaving}
            aria-busy={isSaving}
          >
            {isSaving ? 'Saving…' : 'Save template'}
          </button>
        </div>
      </form>
    </section>
  )
}
