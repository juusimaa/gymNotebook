namespace GymNotebook.Api;

public static class ProgressMetric
{
    // Returns the chart value for one qualifying working set. The caller decides
    // eligibility: loaded exercises need a weight, while the bodyweight series
    // deliberately includes only unloaded sets.
    public static decimal? Calculate(bool isBodyweight, decimal? weight, int reps)
    {
        if (isBodyweight)
        {
            return reps;
        }

        if (!weight.HasValue)
        {
            return null;
        }

        // A single is already a measured maximum. Applying Epley would inflate
        // that observation by 1/30 instead of estimating something unknown.
        return reps == 1 ? weight.Value : weight.Value * (1m + reps / 30m);
    }
}
