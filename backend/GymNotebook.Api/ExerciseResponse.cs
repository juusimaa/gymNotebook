namespace GymNotebook.Api;

// One autocomplete suggestion (GET /exercises) and the body of PATCH /exercises/{id}.
// LastSet is what the suggestion shows next to the name; how "last" is chosen is
// documented on ProjectExerciseResponses in Program.cs.
public record ExerciseResponse(int Id, string Name, bool IsBodyweight, LastSetResponse? LastSet);
