namespace GymNotebook.Tests;

// The throwaway-user conventions every test class shares.
public static class TestUsers
{
    // Any string BCrypt will hash; the tests never care what it is, only that register and
    // login agree on it.
    public const string DefaultPassword = "correct-horse-battery-staple";

    // Every test in a class shares one database, and xUnit may run them in any order, so
    // each registers its own throwaway user rather than relying on a fixed name.
    public static string UniqueUsername() => $"user-{Guid.NewGuid():N}";
}
