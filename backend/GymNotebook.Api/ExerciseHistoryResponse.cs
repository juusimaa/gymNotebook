namespace GymNotebook.Api;

// Exercise metadata travels with its points so a direct history request contains
// everything needed to label the metric and y-axis correctly.
public record ExerciseHistoryResponse(
    int ExerciseId,
    string ExerciseName,
    bool IsBodyweight,
    List<ExerciseHistoryPointResponse> Points);
