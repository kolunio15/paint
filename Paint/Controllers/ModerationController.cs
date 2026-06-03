using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;
using Paint.Services;

namespace Paint.Controllers;

[ApiController]
[Route("api/moderation")]
[Authorize]
public class ModerationController(
    ModerationService moderation,
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db) : ControllerBase
{
    public record ReportRequest(
        string? TargetUserId,
        int? TargetArtworkId,
        int? TargetCommentId,
        int? TargetRoomId,
        string? Reason);

    [HttpPost("report")]
    public async Task<IActionResult> Report([FromBody] ReportRequest req)
    {
        if (req.TargetUserId is null && req.TargetArtworkId is null &&
            req.TargetCommentId is null && req.TargetRoomId is null)
        {
            return BadRequest("No target specified.");
        }

        var reporterId = userManager.GetUserId(User)!;

        // Deduplicate: a user re-reporting the same target while a report is still
        // pending shouldn't flood the queue with identical items.
        var alreadyPending = await db.ModerationQueue.AnyAsync(q =>
            q.Status == QueueItemStatus.Pending &&
            q.ReportedById == reporterId &&
            q.TargetUserId == req.TargetUserId &&
            q.TargetArtworkId == req.TargetArtworkId &&
            q.TargetCommentId == req.TargetCommentId &&
            q.TargetRoomId == req.TargetRoomId);

        if (alreadyPending)
            return Ok();

        await moderation.EnqueueAsync(
            QueueItemType.UserReport,
            targetUserId: req.TargetUserId,
            targetArtworkId: req.TargetArtworkId,
            targetCommentId: req.TargetCommentId,
            targetRoomId: req.TargetRoomId,
            reportedById: reporterId,
            reportReason: req.Reason);

        return Ok();
    }

    public record EnqueueRequest(
        string? TargetUserId,
        int? TargetArtworkId,
        int? TargetCommentId,
        int? TargetRoomId,
        string? Reason);

    [HttpPost("enqueue")]
    [Authorize(Roles = "Admin,Moderator")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enqueue([FromBody] EnqueueRequest req)
    {
        if (req.TargetUserId is null && req.TargetArtworkId is null &&
            req.TargetCommentId is null && req.TargetRoomId is null)
        {
            return BadRequest("No target specified.");
        }

        await moderation.EnqueueAsync(
            QueueItemType.UserReport,
            targetUserId: req.TargetUserId,
            targetArtworkId: req.TargetArtworkId,
            targetCommentId: req.TargetCommentId,
            targetRoomId: req.TargetRoomId,
            reportedById: userManager.GetUserId(User),
            reportReason: req.Reason);

        return Ok();
    }

    public record HideRequest(
        int? ArtworkId,
        int? CommentId,
        int? RoomId,
        string? Reason);

    [HttpPost("hide")]
    [Authorize(Roles = "Admin,Moderator")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Hide([FromBody] HideRequest req)
    {
        if (req.ArtworkId is null && req.CommentId is null && req.RoomId is null)
            return BadRequest("No target specified.");

        if (!User.IsInRole("Admin"))
        {
            string? authorId = null;
            if (req.ArtworkId.HasValue)
                authorId = (await db.Artworks.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.ArtworkId.Value))?.AuthorId;
            else if (req.CommentId.HasValue)
                authorId = (await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CommentId.Value))?.AuthorId;
            else if (req.RoomId.HasValue)
                authorId = (await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RoomId.Value))?.OwnerId;

            if (authorId is not null)
            {
                var author = await userManager.FindByIdAsync(authorId);
                if (author is not null &&
                    (await userManager.IsInRoleAsync(author, "Admin") || await userManager.IsInRoleAsync(author, "Moderator")))
                    return Forbid();
            }
        }

        var type = req.ArtworkId.HasValue ? ModerationActionType.HideArtwork
            : req.CommentId.HasValue ? ModerationActionType.HideComment
            : ModerationActionType.HideRoom;

        await moderation.HideContentAsync(type, userManager.GetUserId(User)!,
            artworkId: req.ArtworkId, commentId: req.CommentId, roomId: req.RoomId, reason: req.Reason);

        return Ok();
    }

    public record UnhideRequest(int? ArtworkId, int? CommentId, int? RoomId);

    [HttpPost("unhide")]
    [Authorize(Roles = "Admin,Moderator")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unhide([FromBody] UnhideRequest req)
    {
        if (req.ArtworkId is null && req.CommentId is null && req.RoomId is null)
            return BadRequest("No target specified.");

        await moderation.UnhideContentAsync(userManager.GetUserId(User)!,
            artworkId: req.ArtworkId, commentId: req.CommentId, roomId: req.RoomId);

        return Ok();
    }
}
