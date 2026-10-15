namespace GymNotebook.Api;

public record TemplateExerciseResponse(
    int Id,
    int ExerciseId,
    string ExerciseName,
    bool IsBodyweight,
    List<TemplateSetResponse> Sets
);
