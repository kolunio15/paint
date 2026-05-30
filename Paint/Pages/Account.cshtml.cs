using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Paint.Models;

namespace Paint.Pages;

[Authorize]
public class AccountModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AccountModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
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
        if (user is null)
        {
            return Challenge();
        }

        Username     = user.DisplayName ?? user.UserName ?? "Guest";
        UserId       = user.Id;
        Bio          = user.Bio ?? "";
        MemberSince  = user.CreatedAt == default ? DateTime.Today : user.CreatedAt.ToLocalTime();
        LastActive   = user.LastActiveAt?.ToLocalTime() ?? DateTime.Now;
        ArtworkCount = user.Artworks.Count;
        TotalLikes   = user.Votes.Count(vote => vote.Value > 0);
        RoomsJoined  = user.RoomParticipants.Count;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

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
