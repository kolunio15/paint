using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Paint.Models;

namespace Paint.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<PaintRoom> Rooms => Set<PaintRoom>();

    public DbSet<CanvasEvent> CanvasEvents => Set<CanvasEvent>();

    public DbSet<Artwork> Artworks => Set<Artwork>();

    public DbSet<ArtworkComment> ArtworkComments => Set<ArtworkComment>();

    public DbSet<ArtworkVote> ArtworkVotes => Set<ArtworkVote>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PaintRoom>(entity =>
        {
            entity.Property(room => room.Id).HasMaxLength(128);
            entity.Property(room => room.Name).HasMaxLength(100);
            entity.Property(room => room.PasswordHash).HasMaxLength(512);
            entity.HasIndex(room => room.Name);
        });

        builder.Entity<CanvasEvent>(entity =>
        {
            entity.Property(canvasEvent => canvasEvent.RoomId).HasMaxLength(128);
            entity.Property(canvasEvent => canvasEvent.EventType).HasMaxLength(64);
            entity.Property(canvasEvent => canvasEvent.PayloadJson).HasColumnType("TEXT");
            entity.Property(canvasEvent => canvasEvent.UserId).HasMaxLength(450);
            entity.Property(canvasEvent => canvasEvent.UserName).HasMaxLength(256);
            entity.HasIndex(canvasEvent => new { canvasEvent.RoomId, canvasEvent.Id });
            entity
                .HasOne(canvasEvent => canvasEvent.Room)
                .WithMany(room => room.CanvasEvents)
                .HasForeignKey(canvasEvent => canvasEvent.RoomId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Artwork>(entity =>
        {
            entity.Property(artwork => artwork.Title).HasMaxLength(120);
            entity.Property(artwork => artwork.ImageUrl).HasMaxLength(300);
            entity.Property(artwork => artwork.ThumbnailUrl).HasMaxLength(300);
            entity.Property(artwork => artwork.RoomId).HasMaxLength(128);
            entity.Property(artwork => artwork.CreatedByUserId).HasMaxLength(450);
            entity.Property(artwork => artwork.CreatedByUserName).HasMaxLength(256);
            entity.HasIndex(artwork => artwork.CreatedAtUtc);
            entity
                .HasOne(artwork => artwork.Room)
                .WithMany()
                .HasForeignKey(artwork => artwork.RoomId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ArtworkComment>(entity =>
        {
            entity.Property(comment => comment.UserId).HasMaxLength(450);
            entity.Property(comment => comment.UserName).HasMaxLength(256);
            entity.Property(comment => comment.Content).HasMaxLength(2000);
            entity.HasIndex(comment => new { comment.ArtworkId, comment.CreatedAtUtc });
            entity
                .HasOne(comment => comment.Artwork)
                .WithMany(artwork => artwork.Comments)
                .HasForeignKey(comment => comment.ArtworkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ArtworkVote>(entity =>
        {
            entity.HasKey(vote => new { vote.ArtworkId, vote.UserId });
            entity.Property(vote => vote.UserId).HasMaxLength(450);
            entity
                .HasOne(vote => vote.Artwork)
                .WithMany(artwork => artwork.Votes)
                .HasForeignKey(vote => vote.ArtworkId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
