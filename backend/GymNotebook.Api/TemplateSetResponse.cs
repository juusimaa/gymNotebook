namespace GymNotebook.Api;

public record TemplateSetResponse(
    int Id,
    int SetNumber,
    decimal? Weight,
    int Reps,
    bool IsWarmup
);
