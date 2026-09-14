namespace GymNotebook.Api;

public record SetEntryResponse(
    int Id,
    int SetNumber,
    int Reps,
    decimal? Weight,
    bool IsWarmup
);
