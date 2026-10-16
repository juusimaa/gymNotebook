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
    public DbSet<EmailSend> EmailSends => Set<EmailSend>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateExercise> TemplateExercises => Set<TemplateExercise>();
    public DbSet<TemplateSet> TemplateSets => Set<TemplateSet>();

    // Model configuration that conventions can't infer. Anything set here ends up in the
    // migrations, so a change here means a new `dotnet ef migrations add`.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            // One account per address. Register looks the address up first, but two
            // signups at the same instant can both miss; this index makes the second
            // INSERT fail, and the handler treats that as "account exists" (specs/002
            // plan D4). Display names get no index at all: they aren't unique (O1) and
            // nothing looks an account up by one. Both stay `text`; their length limits
            // are input rules, checked in AccountInput, as for every other text column here.
            entity.HasIndex(u => u.Email).IsUnique();

            // Let Postgres stamp the row at insert time — one clock for every row, and
            // EF Core omits the column from the INSERT when the C# value is still default.
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("now()");

            // Unique so two accounts can never share a privacy identity, even across a
            // email address being registered again. Throw-after-save makes EF refuse to write
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

            // The optional-details consent pair follows the same two rules: a trusted,
            // bounded version string, and both fields null or both set (data-model.md →
            // Optional-details consent).
            entity.Property(u => u.OptionalDetailsConsentVersion).HasMaxLength(64);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_users_optional_details_consent_pair",
                "(optional_details_consent_version IS NULL) = (optional_details_consented_at IS NULL)"));

            // Demo accounts only (specs/007 data-model.md): serves the cleanup scan
            // (<= now()) and the live-demo cap count (> now()) without indexing every
            // registered account, whose value is always null.
            entity.HasIndex(u => u.DemoExpiresAt)
                .HasDatabaseName("ix_users_demo_expires_at")
                .HasFilter("demo_expires_at IS NOT NULL");
        });

        modelBuilder.Entity<Workout>(entity =>
        {
            entity.Property(w => w.Revision).HasDefaultValue(1);
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

            // Stored as the lowercase word (SetStatusRules.ToText), readable in psql and
            // in the export, and kept to those three words by a check constraint
            // (specs/006 research R2). The column default makes every set that existed
            // before statuses a done set, with no data step in the migration (FR-012).
            //
            // ValueGeneratedNever undoes what HasDefaultValue implies: EF would otherwise
            // leave the column out of the INSERT whenever the C# value is the enum's CLR
            // default, and that is Planned, so a planned set would be saved as done.
            // The default stays in the schema; EF just always sends the value it has.
            entity.Property(s => s.Status)
                .HasConversion(
                    status => SetStatusRules.ToText(status),
                    text => SetStatusRules.FromText(text))
                .HasColumnType("text")
                .HasDefaultValue(SetStatus.Done)
                .ValueGeneratedNever();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_set_entries_status",
                "status IN ('planned', 'done', 'skipped')"));
        });

        modelBuilder.Entity<Exercise>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.NormalizedName }).IsUnique();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(e => e.UserId);
        });

        // Templates mirror the page tables (specs/006 research R6): cascades go template →
        // blocks → sets, and a block's exercise is Restrict, as on workout_exercises.
        modelBuilder.Entity<Template>(entity =>
        {
            // Named so the handlers can tell a name clash from any other unique violation
            // (IsUniqueViolation) and answer 409 template_name_taken.
            entity.HasIndex(t => new { t.UserId, t.NormalizedName })
                .IsUnique()
                .HasDatabaseName("ix_templates_user_id_normalized_name");
            entity.Property(t => t.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(t => t.UserId);
        });

        modelBuilder.Entity<TemplateExercise>(entity =>
        {
            entity.HasOne<Template>()
                .WithMany(t => t.Exercises)
                .HasForeignKey(te => te.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict: an exercise used in a template can't vanish under it. Account
            // deletion removes templates before exercises for this reason, and an exercise
            // merge re-points these rows first (research R7).
            entity.HasOne<Exercise>()
                .WithMany()
                .HasForeignKey(te => te.ExerciseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TemplateSet>(entity =>
        {
            entity.Property(s => s.Weight).HasPrecision(6, 2);

            entity.HasOne<TemplateExercise>()
                .WithMany(te => te.Sets)
                .HasForeignKey(s => s.TemplateExerciseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.TemplateExerciseId, s.SetNumber });
        });

        modelBuilder.Entity<EmailSend>(entity =>
        {
            // EmailCaps runs three statements per claim: delete rows older than 24 h
            // (range on sent_at), count the last 24 h (sent_at again), and count one
            // recipient's rows (recipient_hash). One index each keeps all three cheap.
            entity.HasIndex(s => s.RecipientHash);
            entity.HasIndex(s => s.SentAt);
        });
    }
}
