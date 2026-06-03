using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;

namespace Paint.Services;

public class ModerationService(ApplicationDbContext db)
{
    public async Task EnqueueAsync(QueueItemType type, string? targetUserId = null,
        int? targetArtworkId = null, int? targetCommentId = null, int? targetRoomId = null,
        string? reportedById = null, string? reportReason = null)
    {
        db.ModerationQueue.Add(new ModerationQueueItem
        {
            Type = type,
            Status = QueueItemStatus.Pending,
            TargetUserId = targetUserId,
            TargetArtworkId = targetArtworkId,
            TargetCommentId = targetCommentId,
            TargetRoomId = targetRoomId,
            ReportedById = reportedById,
            ReportReason = reportReason,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task HideContentAsync(ModerationActionType type, string moderatorId,
        int? artworkId = null, int? commentId = null, int? roomId = null, string? reason = null)
    {
        var now = DateTime.UtcNow;

        if (artworkId.HasValue)
        {
            var artwork = await db.Artworks.FindAsync(artworkId.Value);
            if (artwork is not null) { artwork.IsHidden = true; artwork.HiddenAt = now; artwork.HiddenById = moderatorId; }
        }
        if (commentId.HasValue)
        {
            var comment = await db.Comments.FindAsync(commentId.Value);
            if (comment is not null) { comment.IsHidden = true; comment.HiddenAt = now; comment.HiddenById = moderatorId; }
        }
        if (roomId.HasValue)
        {
            var room = await db.Rooms.FindAsync(roomId.Value);
            if (room is not null) { room.IsHidden = true; room.HiddenAt = now; room.HiddenById = moderatorId; }
        }

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = moderatorId,
            Type = type,
            TargetArtworkId = artworkId,
            TargetCommentId = commentId,
            TargetRoomId = roomId,
            Reason = reason,
            CreatedAt = now
        });

        await db.SaveChangesAsync();
    }

    public async Task UnhideContentAsync(string moderatorId,
        int? artworkId = null, int? commentId = null, int? roomId = null)
    {
        if (artworkId.HasValue)
        {
            var artwork = await db.Artworks.FindAsync(artworkId.Value);
            if (artwork is not null) { artwork.IsHidden = false; artwork.HiddenAt = null; artwork.HiddenById = null; }
        }
        if (commentId.HasValue)
        {
            var comment = await db.Comments.FindAsync(commentId.Value);
            if (comment is not null) { comment.IsHidden = false; comment.HiddenAt = null; comment.HiddenById = null; }
        }
        if (roomId.HasValue)
        {
            var room = await db.Rooms.FindAsync(roomId.Value);
            if (room is not null) { room.IsHidden = false; room.HiddenAt = null; room.HiddenById = null; }
        }

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = moderatorId,
            Type = ModerationActionType.Unhide,
            TargetArtworkId = artworkId,
            TargetCommentId = commentId,
            TargetRoomId = roomId,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    public IQueryable<ModerationQueueItem> GetQueueQuery(bool includeResolved = false)
    {
        var query = db.ModerationQueue
            .Include(q => q.TargetUser)
            .Include(q => q.TargetArtwork).ThenInclude(a => a!.Author)
            .Include(q => q.TargetComment).ThenInclude(c => c!.Author)
            .Include(q => q.TargetRoom).ThenInclude(r => r!.Owner)
            .Include(q => q.ReportedBy)
            .Include(q => q.ReviewedBy)
            .AsQueryable();

        if (!includeResolved)
            query = query.Where(q => q.Status == QueueItemStatus.Pending || q.Status == QueueItemStatus.Escalated);

        return query.OrderByDescending(q => q.CreatedAt);
    }
}
