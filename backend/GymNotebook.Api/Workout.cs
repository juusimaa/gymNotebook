namespace GymNotebook.Api;

public class Workout
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int Revision { get; set; } = 1;
    // Both null on a prepared page: written before the gym, not started yet
    // (specs/006 research R1). POST /workouts/{id}/start fills them in together, and the
    // check constraint ck_workouts_started_pair keeps them paired.
    public DateOnly? Date { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Title { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public decimal? BodyweightKg { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<WorkoutExercise> WorkoutExercises { get; set; } = [];
}
