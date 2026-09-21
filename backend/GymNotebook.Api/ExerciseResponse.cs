namespace GymNotebook.Api;

// One exercise-index row/autocomplete suggestion (GET /exercises) and the body of
// PATCH /exercises/{id}. SessionCount counts distinct workout pages, while LastSet
// is the autocomplete hint; both projections are documented in Program.cs.
public record ExerciseResponse(
    int Id,
    string Name,
    bool IsBodyweight,
    int SessionCount,
    LastSetResponse? LastSet);
