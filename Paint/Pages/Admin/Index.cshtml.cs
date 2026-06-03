using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;

namespace Paint.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AdminIndexModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : PageModel
{
    public int TotalUsers { get; set; }
    public int BannedUsers { get; set; }
    public int PendingQueue { get; set; }
    public int EscalatedQueue { get; set; }
    public int ModeratorsCount { get; set; }
    public int ActionsLast24h { get; set; }
    public int HiddenArtworks { get; set; }
    public int HiddenRooms { get; set; }
    public int HiddenComments { get; set; }
    public int ScheduledDeletions { get; set; }
    public List<ModerationQueueItem> EscalatedItems { get; set; } = [];

    public async Task OnGetAsync()
    {
        TotalUsers = await db.Users.CountAsync();
        BannedUsers = await db.Users.OfType<ApplicationUser>().CountAsync(u => u.IsBanned);
        PendingQueue = await db.ModerationQueue.CountAsync(q => q.Status == QueueItemStatus.Pending);
        EscalatedQueue = await db.ModerationQueue.CountAsync(q => q.Status == QueueItemStatus.Escalated);
        ModeratorsCount = (await userManager.GetUsersInRoleAsync("Moderator")).Count;
        ActionsLast24h = await db.ModerationActions.CountAsync(a => a.CreatedAt >= DateTime.UtcNow.AddHours(-24));
        HiddenArtworks = await db.Artworks.CountAsync(a => a.IsHidden && !a.ScheduledDeletionAt.HasValue);
        HiddenRooms = await db.Rooms.CountAsync(r => r.IsHidden && !r.ScheduledDeletionAt.HasValue);
        HiddenComments = await db.Comments.CountAsync(c => c.IsHidden && !c.ScheduledDeletionAt.HasValue);
        ScheduledDeletions = await db.Artworks.CountAsync(a => a.ScheduledDeletionAt.HasValue)
            + await db.Rooms.CountAsync(r => r.ScheduledDeletionAt.HasValue)
            + await db.Comments.CountAsync(c => c.ScheduledDeletionAt.HasValue);
        EscalatedItems = await db.ModerationQueue
            .Include(q => q.TargetUser)
            .Include(q => q.TargetArtwork)
            .Include(q => q.TargetRoom)
            .Where(q => q.Status == QueueItemStatus.Escalated)
            .OrderByDescending(q => q.CreatedAt)
            .Take(10)
            .ToListAsync();
    }
}
