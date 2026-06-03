using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;
using Paint.Services;

namespace Paint.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AdminContentModel(
    ApplicationDbContext db,
    ModerationService moderation,
    UserManager<ApplicationUser> userManager) : PageModel
{
    public string Tab { get; set; } = "artworks";
    public string? Search { get; set; }
    public List<Artwork> Artworks { get; set; } = [];
    public List<Room> Rooms { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];

    public async Task OnGetAsync(string tab = "artworks", string? search = null)
    {
        Tab = tab;
        Search = search;

        if (tab == "artworks")
        {
            var q = db.Artworks.Include(a => a.Author).AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(a => a.Title.Contains(search) || a.Author!.UserName!.Contains(search));
            Artworks = await q.OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync();
        }
        else if (tab == "rooms")
        {
            var q = db.Rooms.Include(r => r.Owner).AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(r => r.Name.Contains(search) || r.Owner!.UserName!.Contains(search));
            Rooms = await q.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();
        }
        else if (tab == "comments")
        {
            var q = db.Comments.Include(c => c.Author).AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(c => c.Content.Contains(search) || c.Author!.UserName!.Contains(search));
            Comments = await q.OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync();
        }
    }

    public async Task<IActionResult> OnPostHideArtworkAsync(int id, string? reason)
    {
        await moderation.HideContentAsync(ModerationActionType.HideArtwork, userManager.GetUserId(User)!, artworkId: id, reason: reason);
        return RedirectToPage(new { tab = "artworks", search = Search });
    }

    public async Task<IActionResult> OnPostUnhideArtworkAsync(int id)
    {
        await moderation.UnhideContentAsync(userManager.GetUserId(User)!, artworkId: id);
        return RedirectToPage(new { tab = "artworks", search = Search });
    }

    public async Task<IActionResult> OnPostHideRoomAsync(int id, string? reason)
    {
        await moderation.HideContentAsync(ModerationActionType.HideRoom, userManager.GetUserId(User)!, roomId: id, reason: reason);
        return RedirectToPage(new { tab = "rooms", search = Search });
    }

    public async Task<IActionResult> OnPostUnhideRoomAsync(int id)
    {
        await moderation.UnhideContentAsync(userManager.GetUserId(User)!, roomId: id);
        return RedirectToPage(new { tab = "rooms", search = Search });
    }

    public async Task<IActionResult> OnPostHideCommentAsync(int id, string? reason)
    {
        await moderation.HideContentAsync(ModerationActionType.HideComment, userManager.GetUserId(User)!, commentId: id, reason: reason);
        return RedirectToPage(new { tab = "comments", search = Search });
    }

    public async Task<IActionResult> OnPostUnhideCommentAsync(int id)
    {
        await moderation.UnhideContentAsync(userManager.GetUserId(User)!, commentId: id);
        return RedirectToPage(new { tab = "comments", search = Search });
    }

    public async Task<IActionResult> OnPostScheduleDeleteArtworkAsync(int id)
    {
        var item = await db.Artworks.FindAsync(id);
        if (item is null) return NotFound();
        item.IsHidden = true;
        item.ScheduledDeletionAt = DateTime.UtcNow.AddHours(24);
        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.ScheduleDelete,
            TargetArtworkId = id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = item.ScheduledDeletionAt
        });
        await db.SaveChangesAsync();
        return RedirectToPage(new { tab = "artworks", search = Search });
    }

    public async Task<IActionResult> OnPostCancelDeleteArtworkAsync(int id)
    {
        var item = await db.Artworks.FindAsync(id);
        if (item is null) return NotFound();
        item.ScheduledDeletionAt = null;
        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.CancelDelete,
            TargetArtworkId = id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return RedirectToPage(new { tab = "artworks", search = Search });
    }

    public async Task<IActionResult> OnPostScheduleDeleteRoomAsync(int id)
    {
        var item = await db.Rooms.FindAsync(id);
        if (item is null) return NotFound();
        item.IsHidden = true;
        item.ScheduledDeletionAt = DateTime.UtcNow.AddHours(24);
        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.ScheduleDelete,
            TargetRoomId = id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = item.ScheduledDeletionAt
        });
        await db.SaveChangesAsync();
        return RedirectToPage(new { tab = "rooms", search = Search });
    }

    public async Task<IActionResult> OnPostCancelDeleteRoomAsync(int id)
    {
        var item = await db.Rooms.FindAsync(id);
        if (item is null) return NotFound();
        item.ScheduledDeletionAt = null;
        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.CancelDelete,
            TargetRoomId = id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return RedirectToPage(new { tab = "rooms", search = Search });
    }

    public async Task<IActionResult> OnPostScheduleDeleteCommentAsync(int id)
    {
        var item = await db.Comments.FindAsync(id);
        if (item is null) return NotFound();
        item.IsHidden = true;
        item.ScheduledDeletionAt = DateTime.UtcNow.AddHours(24);
        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.ScheduleDelete,
            TargetCommentId = id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = item.ScheduledDeletionAt
        });
        await db.SaveChangesAsync();
        return RedirectToPage(new { tab = "comments", search = Search });
    }

    public async Task<IActionResult> OnPostCancelDeleteCommentAsync(int id)
    {
        var item = await db.Comments.FindAsync(id);
        if (item is null) return NotFound();
        item.ScheduledDeletionAt = null;
        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.CancelDelete,
            TargetCommentId = id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return RedirectToPage(new { tab = "comments", search = Search });
    }
}
