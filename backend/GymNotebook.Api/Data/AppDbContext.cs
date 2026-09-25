using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GymNotebook.Api.Data;

// The EF Core unit of work: one instance per HTTP request (scoped, see AddDbContext in
// Program.cs) that holds the connection and tracks which entities changed until
// SaveChangesAsync writes them out. The primary constructor just forwards the options
// (provider, connection string, naming convention) that AddDbContext configured.
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // One DbSet per table. Expression-bodied over Set<T>() rather than an auto-property
    // because EF Core fills a `{ get; set; }` DbSet in by reflection after construction —
    // the compiler can't see that and would warn (CS8618) about a non-nullable property
    // left null. This form has no field to be null.
    public DbSet<User> Users => Set<User>();
    public DbSet<Workout> Workouts => Set<Workout>();
    public DbSet<WorkoutExercise> WorkoutExercises => Set<WorkoutExercise>();
    public DbSet<SetEntry> SetEntries => Set<SetEntry>();
    public DbSet<Exercise> Exercises => Set<Exercise>();

    // Model configuration that conventions can't infer. Anything set here ends up in the
    // migrations, so a change here means a new `dotnet ef migrations add`.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            // The database-level guarantee behind /auth/register's 409 pre-check: two
            // rows with the same username can't exist even if the check is raced.
            entity.HasIndex(u => u.Username).IsUnique();

            // Let Postgres stamp the row at insert time — one clock for every row, and
            // EF Core omits the column from the INSERT when the C# value is still default.
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("now()");

            // Unique so two accounts can never share a privacy identity, even across a
            // username being registered again. Throw-after-save makes EF refuse to write
            // a changed value, backing up the entity's init-only setter at runtime.
            entity.HasIndex(u => u.PrivacyAccountId).IsUnique();
            entity.Property(u => u.PrivacyAccountId)
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

            // Notice versions are short identifiers from trusted configuration, never
            // client text; 64 is the bound data-model.md proposes.
            entity.Property(u => u.AcknowledgedPrivacyNoticeVersion).HasMaxLength(64);

            // "Both acknowledgement fields are null or both populated" (data-model.md),
            // enforced by the database so no code path can store half an acknowledgement.
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_users_privacy_notice_acknowledgement_pair",
                "(acknowledged_privacy_notice_version IS NULL) = (privacy_notice_acknowledged_at IS NULL)"));
        });

        modelBuilder.Entity<Workout>(entity =>
        {
            entity.Property(w => w.BodyweightKg).HasPrecision(5, 2);
            entity.Property(w => w.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(w => w.UserId);
        });

        modelBuilder.Entity<WorkoutExercise>(entity =>
        {
            entity.HasOne<Workout>()
                .WithMany(w => w.WorkoutExercises)
                .HasForeignKey(we => we.WorkoutId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Exercise>()
                .WithMany()
                .HasForeignKey(we => we.ExerciseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SetEntry>(entity =>
        {
            entity.Property(s => s.Weight).HasPrecision(6, 2);

            entity.HasOne<WorkoutExercise>()
                .WithMany(we => we.SetEntries)
                .HasForeignKey(s => s.WorkoutExerciseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.WorkoutExerciseId, s.SetNumber });
        });

        modelBuilder.Entity<Exercise>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.NormalizedName }).IsUnique();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(e => e.UserId);
        });
    }
}
