using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Paint.Models;

namespace Paint.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<Artwork> Artworks { get; set; }
    public DbSet<Comment> Comments { get; set; }
    public DbSet<Room> Rooms { get; set; }
    public DbSet<Vote> Votes { get; set; }
    public DbSet<RoomParticipant> RoomParticipants { get; set; }
    public DbSet<CanvasEvent> CanvasEvents { get; set; }
    public DbSet<ModerationQueueItem> ModerationQueue { get; set; }
    public DbSet<ModerationAction> ModerationActions { get; set; }
    public DbSet<BannedIdentifier> BannedIdentifiers { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RoomParticipant>()
            .HasKey(rp => new { rp.RoomId, rp.UserId });

        builder.Entity<Vote>()
            .HasIndex(v => new { v.ArtworkId, v.UserId })
            .IsUnique();
    }
}
