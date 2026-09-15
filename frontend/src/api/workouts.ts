interface SetEntryResponse {
  id: number
  setNumber: number
  reps: number
  weight: number | null
  isWarmup: boolean
}

interface WorkoutExerciseResponse {
  id: number
  exerciseId: number
  exerciseName: string
  isBodyweight: boolean
  sets: SetEntryResponse[]
}

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

export interface CreateWorkoutRequest {
  date: string
  startedAt: string
  title: string | null
  bodyweightKg: number | null
  location: string | null
  notes: string | null
}

export interface UpdateWorkoutRequest {
  date?: string
  startedAt?: string
  endedAt?: string | null
  title?: string | null
  bodyweightKg?: number | null
  location?: string | null
  notes?: string | null
}

export interface PutWorkoutExercisesRequest {
  exercises: PutWorkoutExerciseInput[]
}

export interface PutWorkoutExerciseInput {
  exerciseName: string
  sets: PutSetInput[]
}

export interface PutSetInput {
  weight: number | null
  reps: number
  isWarmup: boolean
}
