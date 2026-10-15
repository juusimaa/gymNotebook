namespace GymNotebook.Api;

// A named routine to prepare pages from (specs/006 user story 4). Not a page: no date,
// state, revision or optional details (research R6). The name is unique per user after
// the same normalization as exercise names; a clash is refused, never merged.
public class Template
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<TemplateExercise> Exercises { get; set; } = [];
}
