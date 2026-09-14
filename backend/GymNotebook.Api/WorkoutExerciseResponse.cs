namespace GymNotebook.Api;

public record WorkoutExerciseResponse(
    int Id,
    int ExerciseId,
    string ExerciseName,
    List<SetEntryResponse> Sets
);
