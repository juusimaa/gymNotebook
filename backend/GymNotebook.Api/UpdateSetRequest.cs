namespace GymNotebook.Api;

// Deliberately NOT the sparse "null means leave alone" shape UpdateWorkoutRequest and
// UpdateExerciseRequest use. Weight is nullable because null is a legitimate *target*
// state (an unloaded bodyweight set), so a plain nullable field can't distinguish
// "don't touch weight" from "clear it". This PATCH therefore always applies all three
// fields: the caller resends the set's whole mutable content. SetNumber and
// WorkoutExerciseId are server-owned and aren't here at all.
public record UpdateSetRequest(decimal? Weight, int Reps, bool IsWarmup);
