using System.Text.RegularExpressions;

namespace GymNotebook.Api;

public static partial class ExerciseNameNormalizer
{
    public static string Normalize(string name) => MyRegex().Replace(name.Trim().ToLowerInvariant(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex MyRegex();
}
