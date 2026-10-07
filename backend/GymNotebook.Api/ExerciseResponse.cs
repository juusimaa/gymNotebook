namespace GymNotebook.Api;

// One exercise-index row/autocomplete suggestion (GET /exercises) and the body of
// PATCH /exercises/{id}. SessionCount counts distinct workout pages, while LastSet
// is the autocomplete hint and FirstSet the opening set of that same latest session;
// all three projections are documented in Program.cs. FirstSet reuses LastSetResponse
// because it carries exactly the same three figures — only which set is picked differs.
public record ExerciseResponse(
    int Id,
    string Name,
    bool IsBodyweight,
    int SessionCount,
    LastSetResponse? LastSet,
    LastSetResponse? FirstSet);
