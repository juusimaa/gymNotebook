namespace GymNotebook.Api;

// The users table (see PLAN.md, data model). A plain entity class rather than ASP.NET
// Core Identity — hand-rolled on purpose so every column and what it's for is fully
// understood. EF Core maps it by convention; the bits convention can't express live in
// AppDbContext.OnModelCreating.
public class User
{
    // Primary key by EF Core convention (a property named Id); Postgres generates the value.
    public int Id { get; set; }

    // `required` makes the compiler reject an object initializer that leaves these out —
    // a User without a name or a hash is never meaningful, so it can't be constructed.
    public required string Username { get; set; }

    // Only ever a BCrypt hash. The plaintext password is hashed in the register handler
    // and never reaches the entity.
    public required string PasswordHash { get; set; }

    // Embedded in every JWT as the "tv" claim and compared on each request. Bumping this
    // invalidates every token already issued for the user (see PLAN.md, Auth section).
    public int TokenVersion { get; set; }

    // Stamped by the database ("now()" column default, see AppDbContext), never set in C#.
    public DateTimeOffset CreatedAt { get; set; }
}
