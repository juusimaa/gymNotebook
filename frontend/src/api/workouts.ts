import { request } from './client'

// Wire types for /workouts, kept in the same module as the calls that use them.
// ASP.NET serializes the backend's PascalCase record properties as camelCase JSON.
// DateOnly and DateTimeOffset values cross the wire as strings; screens decide how
// to turn those strings into input values or human-readable dates and times.

// A saved set as returned by the API. Set numbers and ids are assigned by the
// server, so they appear in responses but not in PutSetInput below.
interface SetEntryResponse {
  id: number
  setNumber: number
  reps: number
  weight: number | null
  isWarmup: boolean
}

// One exercise block within a workout. This id identifies the block appearance;
// exerciseId identifies the reusable per-user exercise behind it. They can differ
// because one exercise may appear in multiple blocks or workouts.
interface WorkoutExerciseResponse {
  id: number
  exerciseId: number
  exerciseName: string
  isBodyweight: boolean
  sets: SetEntryResponse[]
}

// The full page returned after create/update/bulk-save and by GET /workouts/{id}.
// Optional heading fields use null because the backend returns every property even
// when the user did not supply a value.
export interface WorkoutDetailResponse {
  id: number
  date: string
  startedAt: string
  endedAt: string | null
  title: string | null
  bodyweightKg: number | null
  location: string | null
  notes: string | null
  exercises: WorkoutExerciseResponse[]
}

// POST /workouts creates the page heading first. Exercise blocks are saved in a
// separate bulk PUT once this call returns the new workout id.
export interface CreateWorkoutRequest {
  date: string
  startedAt: string
  title: string | null
  bodyweightKg: number | null
  location: string | null
  notes: string | null
}

// PATCH has two distinct states for nullable properties: undefined means omit the
// JSON property and preserve its current value; null sends an explicit JSON null
// and clears it. Date and startedAt cannot be null because a Workout requires them.
export interface UpdateWorkoutRequest {
  date?: string
  startedAt?: string
  endedAt?: string | null
  title?: string | null
  bodyweightKg?: number | null
  location?: string | null
  notes?: string | null
}

// The new-workout screen owns the complete draft, so saving replaces all blocks
// and sets in one atomic request instead of sending one request per set.
export interface PutWorkoutExercisesRequest {
  exercises: PutWorkoutExerciseInput[]
}

// Names, not exercise ids, are the write contract. The backend normalizes each name,
// finds or creates that user's Exercise, and assigns block position from array order.
export interface PutWorkoutExerciseInput {
  exerciseName: string
  sets: PutSetInput[]
}

// Weight is null for an unloaded bodyweight set. Set number is intentionally absent:
// the backend assigns it from this array's order rather than trusting the client.
export interface PutSetInput {
  weight: number | null
  reps: number
  isWarmup: boolean
}

// The compact row returned by GET /workouts. It contains everything the sessions
// list needs, avoiding one detail request per workout.
export interface WorkoutSummaryResponse {
  id: number
  date: string
  startedAt: string
  endedAt: string | null
  title: string | null
  exerciseCount: number
  setCount: number
  exerciseNames: string[]
}

// Keyset pagination uses the last workout id from the previous page as `before`.
// URLSearchParams handles encoding and makes omission of the optional cursor explicit.
export function listWorkouts(
  limit: number,
  before?: number,
): Promise<WorkoutSummaryResponse[]> {
  const params = new URLSearchParams({ limit: limit.toString() })
  if (before !== undefined) {
    params.set('before', before.toString())
  }
  return request<WorkoutSummaryResponse[]>(`/workouts?${params}`)
}

// Creates an empty workout page with its heading and returns its server-assigned id.
export function createWorkout(
  body: CreateWorkoutRequest,
): Promise<WorkoutDetailResponse> {
  return request<WorkoutDetailResponse>('/workouts', {
    method: 'POST',
    body,
  })
}

// Retrieves the full heading, ordered exercise blocks, and ordered sets for one page.
export function getWorkout(id: number): Promise<WorkoutDetailResponse> {
  return request<WorkoutDetailResponse>(`/workouts/${id}`)
}

// Updates only the properties included in body; see UpdateWorkoutRequest's
// undefined-versus-null distinction above.
export function updateWorkout(
  id: number,
  body: UpdateWorkoutRequest,
): Promise<WorkoutDetailResponse> {
  return request<WorkoutDetailResponse>(`/workouts/${id}`, {
    method: 'PATCH',
    body,
  })
}

// Atomically replaces the workout's entire exercise/set contents. The response is
// the freshly persisted detail, including ids, positions expressed as array order,
// and server-assigned set numbers.
export function replaceWorkoutExercises(
  id: number,
  body: PutWorkoutExercisesRequest,
): Promise<WorkoutDetailResponse> {
  return request<WorkoutDetailResponse>(`/workouts/${id}/exercises`, {
    method: 'PUT',
    body,
  })
}
