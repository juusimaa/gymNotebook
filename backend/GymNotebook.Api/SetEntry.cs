namespace GymNotebook.Api;

public class SetEntry
{
    public int Id { get; set; }
    public int WorkoutExerciseId { get; set; }
    public int SetNumber { get; set; }
    public decimal? Weight { get; set; }
    public int Reps { get; set; }
    public bool IsWarmup { get; set; }
}
