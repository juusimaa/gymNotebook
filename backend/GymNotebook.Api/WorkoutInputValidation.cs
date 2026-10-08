namespace GymNotebook.Api;

// The workout routes accept any non-blank exercise name. Restore uses the same rule
// before normalizing file names, without changing the behavior of either route.
public static class WorkoutInputValidation
{
    public static bool HasExerciseName(string? name) => !string.IsNullOrWhiteSpace(name);
}
