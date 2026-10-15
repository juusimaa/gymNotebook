import { request } from './client'
import type { WorkoutDetailResponse } from './workouts'

// Wire types and calls for /templates (specs/006 contracts/api.md). A template
// is a named set of exercise blocks and sets to plan a session from. Planning
// copies it into a new prepared page; the two are never linked afterwards, so
// editing one leaves the other as it was.

// One row of GET /templates: enough to list it without fetching each one.
export interface TemplateSummaryResponse {
  id: number
  name: string
  exerciseNames: string[]
  exerciseCount: number
  setCount: number
}

// A template's set: no status, since nothing in a template is ever lifted.
export interface TemplateSetResponse {
  id: number
  setNumber: number
  weight: number | null
  reps: number
  isWarmup: boolean
}

export interface TemplateExerciseResponse {
  id: number
  exerciseId: number
  exerciseName: string
  isBodyweight: boolean
  sets: TemplateSetResponse[]
}

export interface TemplateDetailResponse {
  id: number
  name: string
  exercises: TemplateExerciseResponse[]
}

// POST /templates and PUT /templates/{id}: the whole template, blocks in the
// same shape as PUT /workouts/{id}/exercises minus the set status. Exercise
// names are get-or-create, as on pages.
export interface SaveTemplateRequest {
  name: string
  exercises: {
    exerciseName: string
    sets: { weight: number | null; reps: number; isWarmup: boolean }[]
  }[]
}

// The 409 a save gets when the caller already has a template by that name,
// compared the way exercise names are (case and spacing ignored). Nothing is
// written.
export const TEMPLATE_NAME_TAKEN = 'template_name_taken'

// The caller's templates, ordered by name. Unpaged: a handful of routines.
export function listTemplates(): Promise<TemplateSummaryResponse[]> {
  return request<TemplateSummaryResponse[]>('/templates')
}

export function getTemplate(id: number): Promise<TemplateDetailResponse> {
  return request<TemplateDetailResponse>(`/templates/${id}`)
}

export function createTemplate(
  body: SaveTemplateRequest,
): Promise<TemplateDetailResponse> {
  return request<TemplateDetailResponse>('/templates', {
    method: 'POST',
    body,
  })
}

// Replaces the whole template in one transaction. No revision: the last
// write wins (research R9).
export function updateTemplate(
  id: number,
  body: SaveTemplateRequest,
): Promise<TemplateDetailResponse> {
  return request<TemplateDetailResponse>(`/templates/${id}`, {
    method: 'PUT',
    body,
  })
}

// Pages already planned from it stay as they are.
export function deleteTemplate(id: number): Promise<void> {
  return request<void>(`/templates/${id}`, { method: 'DELETE' })
}

// A new prepared page holding a copy of the template, every set planned. Its
// title is the template's name, or null when the account doesn't allow
// optional details.
export function prepareFromTemplate(
  id: number,
): Promise<WorkoutDetailResponse> {
  return request<WorkoutDetailResponse>(`/templates/${id}/prepare`, {
    method: 'POST',
  })
}

// A new template copying every block and set of a page (weight, reps and
// warm-up), whatever each set's status.
export function saveWorkoutAsTemplate(
  workoutId: number,
  name: string,
): Promise<TemplateDetailResponse> {
  return request<TemplateDetailResponse>(`/workouts/${workoutId}/template`, {
    method: 'POST',
    body: { name },
  })
}
