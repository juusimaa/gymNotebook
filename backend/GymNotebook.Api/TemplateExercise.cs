namespace GymNotebook.Api;

// One block of a template, mirroring WorkoutExercise: a bare TemplateId upward, and the
// sets as the only navigation (downward).
public class TemplateExercise
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int ExerciseId { get; set; }
    public int Position { get; set; }
    public ICollection<TemplateSet> Sets { get; set; } = [];
}
