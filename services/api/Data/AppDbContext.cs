using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RagBackend.Api.Models;

namespace RagBackend.Api.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RetrievedSources)
                  .HasColumnType("text[]");
        });

        builder.Entity<Document>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Uploader)
                  .WithMany()
                  .HasForeignKey(e => e.UploaderId)
                  .HasPrincipalKey(u => u.Id);
        });

        builder.Entity<Chunk>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Document)
                  .WithMany(d => d.Chunks)
                  .HasForeignKey(e => e.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.DocumentId, e.Ordinal })
                  .IsUnique();
        });
    }
}
