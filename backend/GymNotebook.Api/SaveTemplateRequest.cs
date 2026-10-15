namespace GymNotebook.Api;

// POST /templates and PUT /templates/{id}: the whole template, in the same block shape
// as PUT /workouts/{id}/exercises. A set's Status is ignored here: templates have none.
public record SaveTemplateRequest(string? Name, List<PutWorkoutExerciseInput>? Exercises);
