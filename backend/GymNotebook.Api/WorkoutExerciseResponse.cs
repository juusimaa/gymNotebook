namespace GymNotebook.Api;

public record WorkoutExerciseResponse(
    int Id,
    int ExerciseId,
    string ExerciseName,
    bool IsBodyweight,
    List<SetEntryResponse> Sets
);
