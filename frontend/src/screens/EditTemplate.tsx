import { useEffect, useRef, useState } from 'react'
import { flushSync } from 'react-dom'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError } from '../api/client'
import {
  updateExercise as updateExerciseRecord,
  type ExerciseResponse,
} from '../api/exercises'
import {
  createTemplate,
  deleteTemplate,
  getTemplate,
  listTemplates,
  startFromTemplate,
  TEMPLATE_NAME_TAKEN,
  updateTemplate,
  type SaveTemplateRequest,
} from '../api/templates'
import { createClientId } from './clientId'
import { ExerciseBlockList } from './ExerciseBlockList'
import { ExercisePicker } from './ExercisePicker'
import {
  createStartTimes,
  createWorkoutExerciseDraft,
  type WorkoutExerciseDraft,
} from './newWorkoutDraft'
import { routeNotice } from './routeNotice'
import {
  createTemplateDraft,
  describeTemplateNameTaken,
  findTakenTemplateName,
  prepareTemplateDraft,
  type PrepareTemplateDraftResult,
} from './templateDraft'
import { focusInput, setFieldId, useExerciseBlocks } from './useExerciseBlocks'
import { useVisualViewportHeight } from './useVisualViewportHeight'
import './NewWorkout.css'
import './Templates.css'

// /templates/new and /templates/:templateId (specs/006 US4, docs/ui/README.md
// → 9. Templates). The page editor's blocks and set rows under a Name field,
// with no dateline, no done controls, no autosave and no stored draft: a
// template is written at leisure and saved explicitly (plan D7).
//
// The key gives each route its own editor, so a new template's empty state
// never carries over to an existing one's.
export default function EditTemplate() {
  const { templateId } = useParams()
  return <TemplateEditor key={templateId ?? 'new'} templateId={templateId} />
}

const NAME_FIELD_ID = 'template-name'
const NAME_ERROR_ID = 'template-name-error'
const SAVE_MESSAGE_ID = 'template-save-message'

// A field a failed save points at: the name, or one set's weight or reps.
type InvalidField =
  | { kind: 'name' }
  | { kind: 'set'; setClientId: string; field: 'weight' | 'reps' }

function TemplateEditor({ templateId }: { templateId: string | undefined }) {
  const navigate = useNavigate()
  const parsedId = Number(templateId)
  const isNew = templateId === undefined
  const hasValidId = Number.isSafeInteger(parsedId) && parsedId > 0
  useVisualViewportHeight()

  const [name, setName] = useState('')
  const [exercises, setExercises] = useState<WorkoutExerciseDraft[]>([])
  const [isLoading, setIsLoading] = useState(!isNew)
  const [loadMessage, setLoadMessage] = useState<string | null>(null)
  const [savingAction, setSavingAction] = useState<
    'save' | 'start' | 'delete' | null
  >(null)
  const [saveMessage, setSaveMessage] = useState<string | null>(null)
  // A taken name is said at the field, not in the footer.
  const [nameMessage, setNameMessage] = useState<string | null>(null)
  const [invalidField, setInvalidField] = useState<InvalidField | null>(null)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const searchRef = useRef<HTMLInputElement>(null)
  const deleteRef = useRef<HTMLButtonElement>(null)
  const keepRef = useRef<HTMLButtonElement>(null)

  // The template's id once it exists on the server: a new one gets it from
  // its first save, so a retry after a later failure updates it rather than
  // creating a second.
  const savedIdRef = useRef<number | null>(isNew ? null : parsedId)
  // What the server holds, as last loaded or saved, so Start session on an
  // unchanged template doesn't write it again first.
  const savedRequestRef = useRef<string | null>(null)
  // Exercises a save created whose bodyweight choice hasn't been stored yet;
  // retried with the next save if the PATCH fails, as the page editor does.
  const pendingBodyweightIds = useRef(new Set<number>())

  const blocks = useExerciseBlocks({
    exercises,
    setExercises,
    // Template rows carry 'planned' (templateDraft.ts); never shown or sent.
    newRowStatus: 'planned',
    onSetChange(setClientId) {
      if (
        invalidField?.kind === 'set' &&
        invalidField.setClientId === setClientId
      ) {
        setInvalidField(null)
        setSaveMessage(null)
      }
    },
  })

  useEffect(() => {
    if (isNew) return
    let cancelled = false

    async function loadTemplate() {
      if (!hasValidId) {
        setLoadMessage('This template does not exist.')
        setIsLoading(false)
        return
      }
      try {
        const template = await getTemplate(parsedId)
        if (cancelled) return
        const draft = createTemplateDraft(template, createClientId)
        const prepared = prepareTemplateDraft(draft)
        savedRequestRef.current = prepared.ok
          ? JSON.stringify(prepared.value)
          : null
        setName(draft.name)
        setExercises(draft.exercises)
      } catch (error: unknown) {
        if (cancelled) return
        setLoadMessage(
          error instanceof ApiError && error.status === 404
            ? 'This template could not be found.'
            : 'This template could not be opened. Please try again.',
        )
      } finally {
        if (!cancelled) setIsLoading(false)
      }
    }

    void loadTemplate()
    return () => {
      cancelled = true
    }
  }, [isNew, hasValidId, parsedId])

  // The delete question replaces nothing on screen but takes the focus, to
  // its safe answer; closing it without deleting hands focus back to Delete.
  const wasConfirmingDelete = useRef(false)
  useEffect(() => {
    if (confirmingDelete) keepRef.current?.focus()
    else if (wasConfirmingDelete.current) deleteRef.current?.focus()
    wasConfirmingDelete.current = confirmingDelete
  }, [confirmingDelete])

  // No "last time" in a template: it isn't a session, and its figures are
  // the routine, not the latest lift. A block starts with one empty set.
  function selectExercise(exercise: ExerciseResponse) {
    blocks.addExerciseBlock(
      createWorkoutExerciseDraft(
        createClientId(),
        createClientId(),
        exercise.id,
        exercise.name,
        exercise.isBodyweight,
        null,
        null,
        'planned',
      ),
    )
  }

  // A name not in the index becomes an exercise when the template is saved.
  function selectNewExercise(exerciseName: string, isBodyweight: boolean) {
    blocks.addExerciseBlock(
      createWorkoutExerciseDraft(
        createClientId(),
        createClientId(),
        null,
        exerciseName,
        isBodyweight,
        null,
        null,
        'planned',
      ),
    )
  }

  function isInvalid(field: InvalidField): boolean {
    if (invalidField === null || invalidField.kind !== field.kind) return false
    if (invalidField.kind === 'set' && field.kind === 'set') {
      return (
        invalidField.setClientId === field.setClientId &&
        invalidField.field === field.field
      )
    }
    return true
  }

  // A failed save marks the field it's about and moves focus there. An
  // empty template's fix is the exercise search.
  function pointAtProblem(
    at: Extract<PrepareTemplateDraftResult, { ok: false }>['at'],
  ) {
    if (at.area === 'name') {
      flushSync(() => setInvalidField({ kind: 'name' }))
      focusInput(NAME_FIELD_ID)
      return
    }
    if (at.area !== 'set') {
      setInvalidField(null)
      if (exercises.length === 0) searchRef.current?.focus()
      return
    }
    const set = exercises[at.exerciseIndex]?.sets[at.setIndex]
    if (set === undefined) return
    flushSync(() =>
      setInvalidField({
        kind: 'set',
        setClientId: set.clientId,
        field: at.field,
      }),
    )
    focusInput(setFieldId(set.clientId, at.field))
  }

  // Saves the template if it differs from what the server holds, and
  // returns its id, or null when it couldn't be saved (and says why).
  async function persist(): Promise<number | null> {
    const prepared = prepareTemplateDraft({ name, exercises })
    if (!prepared.ok) {
      // A missing name is said at the field; the rest in the footer.
      const isName = prepared.at.area === 'name'
      setNameMessage(isName ? prepared.message : null)
      setSaveMessage(isName ? null : prepared.message)
      pointAtProblem(prepared.at)
      return null
    }

    const request: SaveTemplateRequest = prepared.value
    const requestJson = JSON.stringify(request)
    let id = savedIdRef.current

    try {
      if (id === null || requestJson !== savedRequestRef.current) {
        const saved =
          id === null
            ? await createTemplate(request)
            : await updateTemplate(id, request)
        id = saved.id
        savedIdRef.current = saved.id
        savedRequestRef.current = requestJson

        // New exercises start as loaded on the server; the response gives
        // their ids, block for block, so a bodyweight choice can stick.
        exercises.forEach((draft, index) => {
          const savedBlock = saved.exercises[index]
          if (
            draft.exerciseId === null &&
            draft.isBodyweight &&
            savedBlock !== undefined
          ) {
            pendingBodyweightIds.current.add(savedBlock.exerciseId)
          }
        })
      }

      await Promise.all(
        [...pendingBodyweightIds.current].map(async (exerciseId) => {
          await updateExerciseRecord(exerciseId, { isBodyweight: true })
          pendingBodyweightIds.current.delete(exerciseId)
        }),
      )
      return id
    } catch (error: unknown) {
      if (
        error instanceof ApiError &&
        error.status === 409 &&
        error.code === TEMPLATE_NAME_TAKEN
      ) {
        // The input is kept; only the message and the mark are added. The
        // message names the other template as it is spelt, so the list is
        // asked for it; without the list, the typed name will do.
        const existing = await listTemplates().catch(() => [])
        setSaveMessage(null)
        setNameMessage(
          describeTemplateNameTaken(
            findTakenTemplateName(name, existing, savedIdRef.current),
          ),
        )
        pointAtProblem({ area: 'name' })
      } else if (error instanceof ApiError && error.status === 404) {
        setSaveMessage(
          'This template no longer exists. It may have been deleted on another device.',
        )
      } else {
        setSaveMessage('This template could not be saved. Please try again.')
      }
      return null
    }
  }

  async function saveTemplate() {
    if (savingAction !== null) return
    setSavingAction('save')
    const id = await persist()
    if (id === null) {
      setSavingAction(null)
      return
    }
    void navigate('/templates', { state: routeNotice('Template saved.') })
  }

  // Start session copies the template as saved, so it saves first, then
  // starts a session from it with the local date and this moment and opens
  // it in the editor, in progress, to tick the sets off.
  async function startSession() {
    if (savingAction !== null) return
    setSavingAction('start')
    const id = await persist()
    if (id === null) {
      setSavingAction(null)
      return
    }
    const { date, startedAt } = createStartTimes(new Date())
    try {
      const session = await startFromTemplate(id, date, startedAt)
      void navigate(`/workouts/${session.id}/edit`)
    } catch {
      setSavingAction(null)
      setSaveMessage(
        'The template is saved, but the session could not be started. Please try again.',
      )
    }
  }

  async function removeTemplate() {
    const id = savedIdRef.current
    if (id === null || savingAction !== null) return
    setSavingAction('delete')
    try {
      await deleteTemplate(id)
    } catch (error: unknown) {
      // Already gone (deleted on another device) is what was asked for.
      if (!(error instanceof ApiError && error.status === 404)) {
        setSavingAction(null)
        setSaveMessage('This template could not be deleted. Please try again.')
        return
      }
    }
    void navigate('/templates', {
      state: routeNotice('Template deleted. Sessions made from it stay.'),
    })
  }

  if (isLoading) {
    return <main className="page workout-editor-state">Opening template…</main>
  }

  if (loadMessage !== null) {
    return (
      <main className="page workout-editor-state">
        <p className="form-message" role="alert">
          {loadMessage}
        </p>
        <Link className="btn btn-ghost" to="/templates">
          Back to templates
        </Link>
      </main>
    )
  }

  const isSaving = savingAction !== null

  return (
    <main className="page new-workout">
      <header className="new-workout-header">
        <Link className="header-link" to="/templates">
          ← Cancel
        </Link>
        <h1>{isNew ? 'New template' : 'Template'}</h1>
        {isNew ? (
          <span className="new-workout-header-spacer" aria-hidden="true"></span>
        ) : (
          <button
            ref={deleteRef}
            className="header-link new-workout-header-spacer template-delete"
            type="button"
            aria-expanded={confirmingDelete}
            disabled={isSaving}
            onClick={() => setConfirmingDelete(true)}
          >
            Delete
          </button>
        )}
      </header>

      {/* The same band as Cancel's discard question, naming what goes and
          what stays. */}
      {confirmingDelete && (
        <section className="new-workout-discard" role="alert">
          <p>
            {name.trim() === ''
              ? 'Delete this template?'
              : `Delete template ${name.trim()}?`}{' '}
            Sessions made from it stay.
          </p>
          <div>
            <button
              ref={keepRef}
              className="btn btn-ghost"
              type="button"
              onClick={() => setConfirmingDelete(false)}
            >
              Keep
            </button>
            <button
              className="btn btn-secondary"
              type="button"
              disabled={isSaving}
              onClick={() => void removeTemplate()}
            >
              {savingAction === 'delete' ? 'Deleting…' : 'Delete template'}
            </button>
          </div>
        </section>
      )}

      {/* Enter never submits: in the exercise search it would save the
          template mid-search. Save is its button. */}
      <form
        className="new-workout-form"
        onSubmit={(event) => event.preventDefault()}
      >
        <fieldset className="new-workout-fields" disabled={isSaving}>
          <div className="new-workout-content">
            <div className="field template-name-field">
              <label className="label" htmlFor={NAME_FIELD_ID}>
                Name
              </label>
              <input
                className="input"
                id={NAME_FIELD_ID}
                type="text"
                autoComplete="off"
                placeholder="Push A"
                value={name}
                aria-invalid={isInvalid({ kind: 'name' }) || undefined}
                aria-describedby={
                  nameMessage === null ? undefined : NAME_ERROR_ID
                }
                onChange={(event) => {
                  if (isInvalid({ kind: 'name' })) setInvalidField(null)
                  setNameMessage(null)
                  setName(event.target.value)
                }}
              />
              {nameMessage !== null && (
                <p
                  id={NAME_ERROR_ID}
                  className="form-message template-name-error"
                  role="alert"
                >
                  {nameMessage}
                </p>
              )}
            </div>

            <section
              className="new-workout-exercises"
              aria-labelledby="template-exercises-title"
            >
              <h2 id="template-exercises-title" className="visually-hidden">
                Exercises
              </h2>

              {exercises.length === 0 && (
                <p className="muted new-workout-empty">
                  Search below to add the first exercise.
                </p>
              )}

              <ExerciseBlockList
                exercises={exercises}
                blocks={blocks}
                invalidProps={(setClientId, field) =>
                  isInvalid({ kind: 'set', setClientId, field })
                    ? {
                        'aria-invalid': true,
                        'aria-describedby': SAVE_MESSAGE_ID,
                      }
                    : {}
                }
              />

              <ExercisePicker
                inputRef={searchRef}
                onSelect={selectExercise}
                onSelectNew={selectNewExercise}
              />
            </section>
          </div>

          <footer className="new-workout-actions">
            {saveMessage !== null && (
              <p id={SAVE_MESSAGE_ID} className="form-message" role="alert">
                {saveMessage}
              </p>
            )}
            {/* Save outranks Start session here: keeping the routine is
                what this screen is for. */}
            <div className="new-workout-action-buttons">
              <button
                className="btn btn-secondary"
                type="button"
                onClick={() => void startSession()}
              >
                {savingAction === 'start' ? 'Starting…' : 'Start session'}
              </button>
              <button
                className="btn btn-primary"
                type="button"
                onClick={() => void saveTemplate()}
              >
                {savingAction === 'save' ? 'Saving…' : 'Save'}
              </button>
            </div>
          </footer>
        </fieldset>
      </form>
    </main>
  )
}
