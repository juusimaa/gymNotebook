namespace GymNotebook.Api;

public class WorkoutExercise
{
    public int Id { get; set; }
    public int WorkoutId { get; set; }
    public int ExerciseId { get; set; }
    public int Position { get; set; }
}
