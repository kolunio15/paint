namespace Paint.Models;

public class Room
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int MaxUsers { get; set; } = 10;
    public int CanvasWidth { get; set; } = PaintDefaults.CanvasWidth;
    public int CanvasHeight { get; set; } = PaintDefaults.CanvasHeight;
    public bool IsHidden { get; set; }
    public DateTime? HiddenAt { get; set; }
    public string? HiddenById { get; set; }
    public DateTime? ScheduledDeletionAt { get; set; }

    public bool IsProtected { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }

    // Compaction: a raster snapshot of the canvas state up to and including
    // SnapshotGlobalId. Events with a globalId <= SnapshotGlobalId have been
    // flattened into the image and removed from CanvasEvents, keeping the event
    // log and replay time bounded. SnapshotGlobalId == -1 means no snapshot yet.
    public string? SnapshotPath { get; set; }
    public int SnapshotGlobalId { get; set; } = -1;

    public required string OwnerId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;

    public ICollection<RoomParticipant> Participants { get; set; } = [];
    public ICollection<CanvasEvent> CanvasEvents { get; set; } = [];
}
