using LoginAndReg.Models.Entities;
using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Person>()
            .Property(p => p.birth_date)
            .HasColumnType("date");

        modelBuilder.Entity<User>()
            .HasIndex(u => u.login)
            .IsUnique();

        modelBuilder.Entity<UserFile>(entity =>
        {
            entity.Property(f => f.storage_path).IsRequired().HasMaxLength(500);
            entity.Property(f => f.thumbnail_path).HasMaxLength(500);
            entity.Property(f => f.file_name).IsRequired().HasMaxLength(255);

            entity.HasIndex(f => f.user_id);
            entity.HasIndex(f => new { f.user_id, f.file_name }).IsUnique();

            // No FK to Users since step 4.3a: user_id is a string owner id
            // (Auth sub for RS256 tokens, legacy numeric id as a string for
            // HS256 tokens). Auth users are not in this database (ADR 0018).
        });
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Person> Persons => Set<Person>();

    public DbSet<UserFile> UserFiles => Set<UserFile>();
}
