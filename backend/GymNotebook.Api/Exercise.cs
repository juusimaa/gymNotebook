namespace GymNotebook.Api;

public class Exercise
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public bool IsBodyweight { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
