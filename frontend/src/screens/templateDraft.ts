import type {
  SaveTemplateRequest,
  TemplateDetailResponse,
  TemplateSummaryResponse,
} from '../api/templates'
import {
  prepareExerciseInputs,
  type DraftFieldRef,
  type WorkoutExerciseDraft,
} from './newWorkoutDraft'
import { normalizeExerciseName } from './exerciseFormat'
import { formatCount } from './workoutFormat'

// The template editor's draft (specs/006 US4): a name and the same blocks the
// page editor edits, so the shared block list and its helpers work unchanged.
// A template's sets have no status; their rows carry 'planned', the status of
// a set not lifted yet, which the editor never shows and Save never sends.
export interface TemplateDraft {
  name: string
  exercises: WorkoutExerciseDraft[]
}

// A loaded template as a draft. The id factory keeps React keys local and
// makes this conversion deterministic in tests.
export function createTemplateDraft(
  template: TemplateDetailResponse,
  createClientId: () => string,
): TemplateDraft {
  return {
    name: template.name,
    exercises: template.exercises.map((exercise) => ({
      clientId: createClientId(),
      exerciseId: exercise.exerciseId,
      exerciseName: exercise.exerciseName,
      isBodyweight: exercise.isBodyweight,
      // A bodyweight block with any added weight opens with that field
      // showing, as a page's does (createExistingWorkoutDraft).
      isAddedWeightEnabled:
        exercise.isBodyweight &&
        exercise.sets.some((set) => set.weight !== null),
      sets: exercise.sets.map((set) => ({
        clientId: createClientId(),
        weight: set.weight === null ? '' : String(set.weight),
        reps: String(set.reps),
        isWarmup: set.isWarmup,
        status: 'planned',
      })),
    })),
  }
}

export type PrepareTemplateDraftResult =
  | { ok: true; value: SaveTemplateRequest }
  | { ok: false; at: DraftFieldRef | { area: 'name' }; message: string }

// What Save sends, or the first thing that stops it. As strict as a page's
// Save changes: every set needs its reps, and its weight unless bodyweight, so
// a session started from the template is ready to tick off.
export function prepareTemplateDraft(
  draft: TemplateDraft,
): PrepareTemplateDraftResult {
  const name = draft.name.trim()
  if (name === '') {
    return {
      ok: false,
      at: { area: 'name' },
      message: 'Name the template.',
    }
  }

  const exercises = prepareExerciseInputs(draft.exercises)
  if (!exercises.ok) {
    return exercises
  }

  return {
    ok: true,
    value: {
      name,
      // Without status: a template has none.
      exercises: exercises.value.map((exercise) => ({
        exerciseName: exercise.exerciseName,
        sets: exercise.sets.map(({ weight, reps, isWarmup }) => ({
          weight,
          reps,
          isWarmup,
        })),
      })),
    },
  }
}

// The 409 template_name_taken, said at the name field.
export function describeTemplateNameTaken(name: string): string {
  return `You already have a template called ${name.trim()}.`
}

// The name the 409 collided with, as the other template spells it: names
// are compared with case and spacing ignored, so "push  a" is taken by
// "Push A", and the message should say "Push A". The typed name when the
// list doesn't have it (it couldn't be loaded, or the other was renamed).
export function findTakenTemplateName(
  name: string,
  templates: Pick<TemplateSummaryResponse, 'id' | 'name'>[],
  ownId: number | null,
): string {
  const wanted = normalizeExerciseName(name)
  const taken = templates.find(
    (template) =>
      template.id !== ownId && normalizeExerciseName(template.name) === wanted,
  )
  return taken?.name ?? name.trim()
}

// A template row's meta line: "3 exercises · 9 sets".
export function describeTemplateCounts(
  template: Pick<TemplateSummaryResponse, 'exerciseCount' | 'setCount'>,
): string {
  return `${formatCount(template.exerciseCount, 'exercise')} · ${formatCount(template.setCount, 'set')}`
}
