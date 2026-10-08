using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Slideshow.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(u => u.Id).HasMaxLength(64);
            e.Property(u => u.DisplayName).HasMaxLength(256);
            e.Property(u => u.Email).HasMaxLength(320);
        });

        b.Entity<Album>(e =>
        {
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.OwnerId).HasMaxLength(64);
            e.Property(a => a.Name).HasMaxLength(Limits.AlbumNameLength);
            e.Property(a => a.Description).HasMaxLength(Limits.AlbumDescriptionLength);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.OwnerId, a.CreatedAt });
            e.Property(a => a.ShareToken).HasMaxLength(32);
            e.HasIndex(a => a.ShareToken).IsUnique(); // filtered to non-null
        });

        b.Entity<MediaFile>(e =>
        {
            e.Property(m => m.Id).ValueGeneratedNever();
            e.Property(m => m.OwnerId).HasMaxLength(64);
            e.Property(m => m.FileName).HasMaxLength(Limits.FileNameLength);
            e.Property(m => m.ContentType).HasMaxLength(100);
            e.Property(m => m.BlobName).HasMaxLength(200);
            // Albums cascade to media; media has no FK to Users to avoid multiple cascade paths in SQL Server.
            e.HasOne<Album>().WithMany(a => a.Media).HasForeignKey(m => m.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.OwnerId, m.AlbumId, m.Collection });
        });

        // Timestamps are stored as UTC; mark them so when read back (TakenAt is local wall time and left alone).
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        foreach (var property in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.Name == nameof(MediaFile.TakenAt)) continue;
            if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
            else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
        }
    }
}

/// <summary>Used by `dotnet ef migrations add`; migrations target Azure SQL.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=(design-time);Database=slideshow").Options);
}
