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

    // The account's permanent privacy identity (specs/001 data-model.md, research R5).
    // Integer ids can be reallocated after a point-in-time restore rewinds the sequence,
    // and usernames can be registered again after a deletion, so neither can identify
    // "the account that was deleted" in deletion log lines or restore reconciliation. A
    // random UUID can. `required` + `init`: every creation site must supply one (the
    // register handler generates it), and nothing can reassign it afterwards —
    // AppDbContext also makes EF refuse to save a changed value.
    public required Guid PrivacyAccountId { get; init; }

    // The latest privacy notice version this user acknowledged with "Continue", and when.
    // Both null until the first acknowledgement, and always set together (a check
    // constraint in AppDbContext). This records that the notice was shown, not consent.
    public string? AcknowledgedPrivacyNoticeVersion { get; set; }
    public DateTimeOffset? PrivacyNoticeAcknowledgedAt { get; set; }

    // Set only by the operator, by manual SQL during a restore fallback, when a
    // deletion's outcome is unknown (research R6 Q2c). While set, login answers a correct
    // password with 403 account_suspended and any token for the account is rejected.
    public DateTimeOffset? SignInSuspendedAt { get; set; }
}
