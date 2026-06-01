using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;

namespace Paint.Pages;

[Authorize]
public class AccountModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ApplicationDbContext _db;

    public AccountModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext db)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
    }

    public string Username    { get; set; } = "Guest";
    public string UserId      { get; set; } = "";
    public string Bio         { get; set; } = "";
    public DateTime MemberSince { get; set; } = DateTime.Today;
    public DateTime LastActive  { get; set; } = DateTime.Today;

    public int ArtworkCount { get; set; } = 0;
    public int TotalLikes   { get; set; } = 0;
    public int RoomsJoined  { get; set; } = 0;

    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? BioInput    { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        Username    = user.DisplayName ?? user.UserName ?? "Guest";
        UserId      = user.Id;
        Bio         = user.Bio ?? "";
        MemberSince = user.CreatedAt == default ? DateTime.Today : user.CreatedAt.ToLocalTime();
        LastActive  = user.LastActiveAt?.ToLocalTime() ?? DateTime.Now;

        // GetUserAsync nie ładuje właściwości nawigacyjnych, więc odpytujemy bezpośrednio
        ArtworkCount = await _db.Artworks.CountAsync(a => a.AuthorId == user.Id);
        TotalLikes   = await _db.Votes.CountAsync(v => v.UserId == user.Id && v.Value > 0);
        RoomsJoined  = await _db.RoomParticipants.CountAsync(r => r.UserId == user.Id);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        user.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? user.UserName : DisplayName.Trim();
        user.Bio = BioInput;
        await _userManager.UpdateAsync(user);

        TempData["Message"] = "Profile updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await _signInManager.SignOutAsync();
        return RedirectToPage("/Index");
    }
}
