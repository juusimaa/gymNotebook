namespace GymNotebook.Api;

public record TemplateDetailResponse(
    int Id,
    string Name,
    List<TemplateExerciseResponse> Exercises
);
