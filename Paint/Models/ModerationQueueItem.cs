namespace Paint.Models;

public enum QueueItemType
{
    RoomCreated,
    ArtworkPublished,
    CommentPosted,
    UserRegistered,
    UserReport
}

public enum QueueItemStatus
{
    Pending,
    ReviewedOk,
    Escalated,
    AdminResolved
}

public class ModerationQueueItem
{
    public int Id { get; set; }
    public QueueItemType Type { get; set; }
    public QueueItemStatus Status { get; set; } = QueueItemStatus.Pending;

    public string? TargetUserId { get; set; }
    public ApplicationUser? TargetUser { get; set; }

    public int? TargetArtworkId { get; set; }
    public Artwork? TargetArtwork { get; set; }

    public int? TargetCommentId { get; set; }
    public Comment? TargetComment { get; set; }

    public int? TargetRoomId { get; set; }
    public Room? TargetRoom { get; set; }

    public string? ReportedById { get; set; }
    public ApplicationUser? ReportedBy { get; set; }

    public string? ReportReason { get; set; }

    public string? ReviewedById { get; set; }
    public ApplicationUser? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; }
}
