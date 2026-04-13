using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Paint.Pages;

public class AccountModel : PageModel
{
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

    public void OnGet()
    {
        // TODO: replace with real data from your user service / database
        Username     = User.Identity?.Name ?? "Guest";
        UserId       = "a3f9c2b1";  // TODO: real user ID
        Bio          = "I love painting!";
        MemberSince  = new DateTime(1999, 4, 9);
        LastActive   = new DateTime(1999, 4, 9);
        ArtworkCount = 12;
        TotalLikes   = 47;
        RoomsJoined  = 5;
    }

    public IActionResult OnPost()
    {
        // TODO: validate & persist DisplayName, BioInput
        TempData["Message"] = "Profile updated.";
        return RedirectToPage();
    }

    public IActionResult OnPostSignOut()
    {
    return RedirectToPage("/Index");
    }
}