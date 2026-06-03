namespace Paint.Models;

public enum ModerationActionType
{
    HideArtwork,
    HideComment,
    HideRoom,
    TempBan,
    PermBan,
    Unban,
    Unhide,
    PromoteModerator,
    DemoteModerator,
    ScheduleDelete,
    CancelDelete,
    ReviewQueueItem,
    EscalateQueueItem
}

public class ModerationAction
{
    public int Id { get; set; }

    public required string ModeratorId { get; set; }
    public ApplicationUser Moderator { get; set; } = null!;

    public ModerationActionType Type { get; set; }

    public string? TargetUserId { get; set; }
    public ApplicationUser? TargetUser { get; set; }

    public int? TargetArtworkId { get; set; }
    public int? TargetCommentId { get; set; }
    public int? TargetRoomId { get; set; }

    public string? Reason { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
