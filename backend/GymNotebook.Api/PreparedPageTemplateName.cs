namespace GymNotebook.Api;

// specs/006 research R15: prepared pages became templates. A version 2 backup written
// before that may still hold some, and restore turns each into a template named here. The
// RemovePreparedWorkouts migration applied the same rule, in SQL, to the pages already
// stored, so a plan reads the same whichever way it arrived.
public static class PreparedPageTemplateName
{
    // The page's title, or "Plan 2026-10-15" (its UTC creation day) without one.
    public static string Base(string? title, DateTimeOffset? createdAt)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        return createdAt is null ? "Plan" : $"Plan {createdAt.Value.UtcDateTime:yyyy-MM-dd}";
    }

    // The base name, or "Push A (2)", "Push A (3)"… when the account already uses it,
    // compared like exercise names. A clash is never refused or merged here: the lifter
    // didn't choose this name, the conversion did. The chosen name is added to taken.
    public static string Unique(string baseName, ISet<string> takenNormalizedNames)
    {
        var candidate = baseName;
        for (var n = 2; takenNormalizedNames.Contains(ExerciseNameNormalizer.Normalize(candidate)); n++)
        {
            candidate = $"{baseName} ({n})";
        }

        takenNormalizedNames.Add(ExerciseNameNormalizer.Normalize(candidate));
        return candidate;
    }
}
