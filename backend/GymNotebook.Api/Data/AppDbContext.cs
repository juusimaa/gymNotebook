using Microsoft.EntityFrameworkCore;

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
        });
    }
}
