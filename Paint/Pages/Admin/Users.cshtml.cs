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
public class AdminUsersModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    BanService banService) : PageModel
{
    public record UserEntry(ApplicationUser User, List<string> Roles);

    public List<UserEntry> Users { get; set; } = [];
    public string? Search { get; set; }
    public string? Filter { get; set; }
    public int BannedCount { get; set; }

    public async Task OnGetAsync(string? search, string? filter)
    {
        Search = search;
        Filter = filter;
        var query = db.Users.OfType<ApplicationUser>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.UserName!.Contains(search));

        if (filter == "banned")
            query = query.Where(u => u.IsBanned);

        BannedCount = await db.Users.OfType<ApplicationUser>().CountAsync(u => u.IsBanned);
        var users = await query.OrderBy(u => u.UserName).Take(100).ToListAsync();

        foreach (var user in users)
        {
            var roles = (await userManager.GetRolesAsync(user)).ToList();
            Users.Add(new UserEntry(user, roles));
        }
    }

    public async Task<IActionResult> OnPostPromoteAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        await userManager.AddToRoleAsync(user, "Moderator");

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.PromoteModerator,
            TargetUserId = userId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return RedirectToPage(new { search = Search, filter = Filter });
    }

    public async Task<IActionResult> OnPostDemoteAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        await userManager.RemoveFromRoleAsync(user, "Moderator");

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = userManager.GetUserId(User)!,
            Type = ModerationActionType.DemoteModerator,
            TargetUserId = userId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return RedirectToPage(new { search = Search, filter = Filter });
    }

    public async Task<IActionResult> OnPostPermBanAsync(string userId, string? reason)
    {
        var target = await userManager.FindByIdAsync(userId);
        var admin = await userManager.GetUserAsync(User);
        if (target is null || admin is null) return NotFound();

        // ApplyPermBanAsync also bans the target's last-known IP and device token (if recorded).
        await banService.ApplyPermBanAsync(target, admin, reason);

        return RedirectToPage(new { search = Search, filter = Filter });
    }

    public async Task<IActionResult> OnPostUnbanAsync(string userId)
    {
        var target = await userManager.FindByIdAsync(userId);
        var admin = await userManager.GetUserAsync(User);
        if (target is null || admin is null) return NotFound();

        await banService.UnbanAsync(target, admin);
        return RedirectToPage(new { search = Search, filter = Filter });
    }
}
