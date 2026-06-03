using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;

namespace Paint.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AdminActionsModel(ApplicationDbContext db) : PageModel
{
    public List<ModerationAction> Actions { get; set; } = [];

    public async Task OnGetAsync()
    {
        Actions = await db.ModerationActions
            .Include(a => a.Moderator)
            .Include(a => a.TargetUser)
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync();
    }
}
