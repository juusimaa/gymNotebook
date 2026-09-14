namespace GymNotebook.Api;

public record PutWorkoutExerciseInput(string ExerciseName, List<PutSetInput> Sets);
