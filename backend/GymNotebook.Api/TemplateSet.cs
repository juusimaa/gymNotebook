namespace GymNotebook.Api;

// One set of a template block, mirroring SetEntry without a status: a template is only
// ever copied as planned sets (research R8).
public class TemplateSet
{
    public int Id { get; set; }
    public int TemplateExerciseId { get; set; }
    public int SetNumber { get; set; }
    public decimal? Weight { get; set; }
    public int Reps { get; set; }
    public bool IsWarmup { get; set; }
}
