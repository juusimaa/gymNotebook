namespace GymNotebook.Api;

public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }

    // Embedded in every JWT as the "tv" claim and compared on each request. Bumping this
    // invalidates every token already issued for the user (see PLAN.md, Auth section).
    public int TokenVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
