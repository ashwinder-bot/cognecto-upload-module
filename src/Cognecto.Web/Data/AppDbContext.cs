using Cognecto.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Cognecto.Web.Data;

// Maps onto the tables created by database/schema.sql (Supabase Postgres).
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<MigrationUpload> MigrationUploads => Set<MigrationUpload>();
    public DbSet<MigrationRecord> MigrationRecords => Set<MigrationRecord>();
}
