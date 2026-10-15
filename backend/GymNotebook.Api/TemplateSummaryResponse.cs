namespace GymNotebook.Api;

// One row of GET /templates: the name and what it holds, so the list renders without
// fetching each template.
public record TemplateSummaryResponse(
    int Id,
    string Name,
    List<string> ExerciseNames,
    int ExerciseCount,
    int SetCount
);
