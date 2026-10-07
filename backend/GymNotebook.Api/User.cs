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
    // a User without an address, a name or a hash is never meaningful, so it can't be
    // constructed.
    //
    // The address signs the user in (specs/002 FR-001). Always stored trimmed and
    // lowercased (AccountInput.NormalizeEmail), so "Ann@Example.com" and "ann@example.com" are one
    // account; the unique index in AppDbContext is what guarantees it.
    public required string Email { get; set; }

    // Null until the address is confirmed by its link (or by a completed
    // password reset). Set once, never cleared. An unconfirmed account can't sign in and
    // its tokens are rejected (specs/002 plan D5); the API only ever exposes "confirmed or
    // not", the exact time appears only in the export.
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    // The name printed on the notebook's cover. Renamed from Username (specs/002 O1): it
    // no longer signs anyone in and is deliberately *not* unique — a unique name would let
    // signup reveal which names already exist.
    public required string DisplayName { get; set; }

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
    // and email addresses can be registered again after a deletion, so neither can identify
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

    // The consent statement version this user consented to for the optional workout
    // details (title, location, notes, bodyweight), and when (specs/001 FR-029–FR-035).
    // Both null means no consent — refusals and withdrawals aren't recorded, they leave the
    // pair null. Always set together (a check constraint in AppDbContext). Existing
    // accounts start with null: consent is never seeded (FR-035).
    public string? OptionalDetailsConsentVersion { get; set; }
    public DateTimeOffset? OptionalDetailsConsentedAt { get; set; }

    // Set only by the operator, by manual SQL during a restore fallback, when a
    // deletion's outcome is unknown (research R6 Q2c). While set, login answers a correct
    // password with 403 account_suspended and any token for the account is rejected.
    public DateTimeOffset? SignInSuspendedAt { get; set; }
}
