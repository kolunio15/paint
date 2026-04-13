using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Paint.Pages;

public class ArtworkModel : PageModel
{
    // Mock — docelowo pobierać z bazy po Id
    private static readonly List<Artwork> _artworks =
    [
        new(Id: 0, CommentCount: 2, Points: 15,  "Super kciuk!", "mock/kciuk.png"),
        new(Id: 1, CommentCount: 1, Points:  2,  "Mój dom",      "mock/domek.png"),
        new(Id: 2, CommentCount: 0, Points: -10, "Jeż",          "mock/jeż.png"),
		new(Id: 3, CommentCount: 0, Points: 0, "OC DO NOT STEAL",          "mock/transistra.png")
    ];

    public Artwork Art { get; set; } = null!;

    public IActionResult OnGet(int id)
    {
        var art = _artworks.FirstOrDefault(a => a.Id == id);
        if (art is null) return NotFound();
        Art = art;
        return Page();
    }
}