import { request } from './client'

// The most recently logged set shown as an autocomplete hint. Weight is null for
// an unloaded bodyweight set; the whole LastSetResponse is null when the exercise
// has never been logged.
interface LastSetResponse {
  weight: number | null
  reps: number
}

// One user-owned exercise returned by GET /exercises and PATCH /exercises/{id}.
// ASP.NET serializes the backend record's PascalCase properties as camelCase JSON.
export interface ExerciseResponse {
  id: number
  name: string
  isBodyweight: boolean
  lastSet: LastSetResponse | null
}

// PATCH updates only supplied fields. Null is intentionally excluded: the backend
// treats null like an omitted property here, so it cannot clear either value.
interface UpdateExerciseRequest {
  name?: string
  isBodyweight?: boolean
}

// Powers the new-workout autocomplete. URLSearchParams safely encodes spaces and
// punctuation; an empty search returns all of the caller's exercises.
export function searchExercises(search: string): Promise<ExerciseResponse[]> {
  const params = new URLSearchParams({ search })
  return request<ExerciseResponse[]>(`/exercises?${params}`)
}

// Renaming to an existing normalized name merges the exercises on the backend.
// The same call sets the bodyweight flag for a newly created exercise after the
// workout bulk-save response provides its exercise id.
export function updateExercise(
  id: number,
  body: UpdateExerciseRequest,
): Promise<ExerciseResponse> {
  return request<ExerciseResponse>(`/exercises/${id}`, {
    method: 'PATCH',
    body,
  })
}
