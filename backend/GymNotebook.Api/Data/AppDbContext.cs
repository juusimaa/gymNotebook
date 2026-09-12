using Microsoft.EntityFrameworkCore;

namespace GymNotebook.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
}
