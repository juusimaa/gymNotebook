namespace GymNotebook.Api;

public class Workout
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Title { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public decimal? BodyweightKg { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
