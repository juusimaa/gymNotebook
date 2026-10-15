namespace GymNotebook.Api;

public class SetEntry
{
    public int Id { get; set; }
    public int WorkoutExerciseId { get; set; }
    public int SetNumber { get; set; }
    public decimal? Weight { get; set; }
    public int Reps { get; set; }
    public bool IsWarmup { get; set; }
    // Done unless a route says otherwise, matching the column default: every set
    // written before statuses existed was a lifted set (specs/006 FR-012).
    public SetStatus Status { get; set; } = SetStatus.Done;
}
