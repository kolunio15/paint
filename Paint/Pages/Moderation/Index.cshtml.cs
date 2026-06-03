using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;
using Paint.Services;

namespace Paint.Pages.Moderation;

[Authorize(Roles = "Admin,Moderator")]
public class ModerationIndexModel(
    ApplicationDbContext db,
    ModerationService moderation,
    BanService banService,
    UserManager<ApplicationUser> userManager) : PageModel
{
    public List<ModerationQueueItem> Items { get; set; } = [];
    public List<ModerationAction> MyActions { get; set; } = [];
    public int PendingCount { get; set; }
    public int EscalatedCount { get; set; }
    public string Filter { get; set; } = "pending";

    public async Task OnGetAsync(string filter = "pending")
    {
        Filter = filter;
        PendingCount = await db.ModerationQueue.CountAsync(q => q.Status == QueueItemStatus.Pending);
        EscalatedCount = await db.ModerationQueue.CountAsync(q => q.Status == QueueItemStatus.Escalated);

        if (filter == "myactions")
        {
            var currentUserId = userManager.GetUserId(User)!;
            MyActions = await db.ModerationActions
                .Include(a => a.TargetUser)
                .Where(a => a.ModeratorId == currentUserId)
                .OrderByDescending(a => a.CreatedAt)
                .Take(50)
                .ToListAsync();
            return;
        }

        var query = moderation.GetQueueQuery(includeResolved: filter == "all");

        if (filter == "pending")
            query = query.Where(q => q.Status == QueueItemStatus.Pending);
        else if (filter == "escalated")
            query = query.Where(q => q.Status == QueueItemStatus.Escalated);

        Items = await query.ToListAsync();
    }

    public async Task<IActionResult> OnPostReviewOkAsync(int itemId)
    {
        var item = await db.ModerationQueue.FindAsync(itemId);
        if (item is null) return NotFound();

        var wasEscalated = item.Status == QueueItemStatus.Escalated;
        item.Status = wasEscalated && User.IsInRole("Admin")
            ? QueueItemStatus.AdminResolved
            : QueueItemStatus.ReviewedOk;
        item.ReviewedById = userManager.GetUserId(User);
        item.ReviewedAt = DateTime.UtcNow;

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.ReviewQueueItem,
            TargetUserId = item.TargetUserId,
            TargetArtworkId = item.TargetArtworkId,
            TargetCommentId = item.TargetCommentId,
            TargetRoomId = item.TargetRoomId,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostHideArtworkAsync(int itemId, int artworkId, string? reason)
    {
        if (!User.IsInRole("Admin"))
        {
            var artwork = await db.Artworks.FindAsync(artworkId);
            if (artwork is not null)
            {
                var author = await userManager.FindByIdAsync(artwork.AuthorId);
                if (author is not null && await IsProtectedStaffAsync(author)) return Forbid();
            }
        }
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.HideContentAsync(ModerationActionType.HideArtwork, moderatorId, artworkId: artworkId, reason: reason);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostHideCommentAsync(int itemId, int commentId, string? reason)
    {
        if (!User.IsInRole("Admin"))
        {
            var comment = await db.Comments.FindAsync(commentId);
            if (comment is not null)
            {
                var author = await userManager.FindByIdAsync(comment.AuthorId);
                if (author is not null && await IsProtectedStaffAsync(author)) return Forbid();
            }
        }
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.HideContentAsync(ModerationActionType.HideComment, moderatorId, commentId: commentId, reason: reason);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostHideRoomAsync(int itemId, int roomId, string? reason)
    {
        if (!User.IsInRole("Admin"))
        {
            var room = await db.Rooms.FindAsync(roomId);
            if (room is not null)
            {
                var owner = await userManager.FindByIdAsync(room.OwnerId);
                if (owner is not null && await IsProtectedStaffAsync(owner)) return Forbid();
            }
        }
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.HideContentAsync(ModerationActionType.HideRoom, moderatorId, roomId: roomId, reason: reason);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostUnhideArtworkAsync(int itemId, int artworkId)
    {
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.UnhideContentAsync(moderatorId, artworkId: artworkId);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostUnhideCommentAsync(int itemId, int commentId)
    {
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.UnhideContentAsync(moderatorId, commentId: commentId);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostUnhideRoomAsync(int itemId, int roomId)
    {
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.UnhideContentAsync(moderatorId, roomId: roomId);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostTempBanAsync(int itemId, string userId, int hours, string? reason)
    {
        var target = await userManager.FindByIdAsync(userId);
        var moderator = await userManager.GetUserAsync(User);
        if (target is null || moderator is null) return NotFound();

        if (!User.IsInRole("Admin") && await IsProtectedStaffAsync(target))
            return Forbid();

        await banService.ApplyTempBanAsync(target, moderator, DateTime.UtcNow.AddHours(hours), reason: reason);
        return RedirectToPage(new { filter = Filter });
    }

    public async Task<IActionResult> OnPostUnbanFromActionsAsync(string userId)
    {
        var target = await userManager.FindByIdAsync(userId);
        var moderator = await userManager.GetUserAsync(User);
        if (target is null || moderator is null) return NotFound();
        await banService.UnbanAsync(target, moderator);
        return RedirectToPage(new { filter = "myactions" });
    }

    public async Task<IActionResult> OnPostUnhideArtworkDirectAsync(int artworkId)
    {
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.UnhideContentAsync(moderatorId, artworkId: artworkId);
        return RedirectToPage(new { filter = "myactions" });
    }

    public async Task<IActionResult> OnPostUnhideCommentDirectAsync(int commentId)
    {
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.UnhideContentAsync(moderatorId, commentId: commentId);
        return RedirectToPage(new { filter = "myactions" });
    }

    public async Task<IActionResult> OnPostUnhideRoomDirectAsync(int roomId)
    {
        var moderatorId = userManager.GetUserId(User)!;
        await moderation.UnhideContentAsync(moderatorId, roomId: roomId);
        return RedirectToPage(new { filter = "myactions" });
    }

    public async Task<IActionResult> OnPostEscalateAsync(int itemId, string? note)
    {
        var item = await db.ModerationQueue.FindAsync(itemId);
        if (item is null) return NotFound();

        item.Status = QueueItemStatus.Escalated;
        item.ReviewedById = userManager.GetUserId(User);
        item.ReviewedAt = DateTime.UtcNow;
        item.ReviewNote = string.IsNullOrWhiteSpace(note)
            ? "Escalated to admin"
            : $"Escalated: {note.Trim()}";

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.EscalateQueueItem,
            TargetUserId = item.TargetUserId,
            TargetArtworkId = item.TargetArtworkId,
            TargetCommentId = item.TargetCommentId,
            TargetRoomId = item.TargetRoomId,
            Reason = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        return RedirectToPage(new { filter = Filter });
    }

    // A moderator may not act on content authored by admins or other moderators; admins are unrestricted.
    private async Task<bool> IsProtectedStaffAsync(ApplicationUser user) =>
        await userManager.IsInRoleAsync(user, "Admin") || await userManager.IsInRoleAsync(user, "Moderator");
}
