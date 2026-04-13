using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Paint.Pages;

public record Artwork(int Id, int CommentCount, int Points, string Title, string ImageUrl);

public class IndexModel : PageModel
{
    public List<Artwork> Artworks { get; set; } = [
        new(Id: 0, CommentCount: 2, Points: 15,  "Super kciuk!", "mock/kciuk.png"), 
        new(Id: 1, CommentCount: 1, Points:  2,  "Mój dom",      "mock/domek.png"), 
        new(Id: 2, CommentCount: 0, Points: -10, "Jeż",          "mock/jeż.png"),
		new(Id: 3, CommentCount: 0, Points: 0, "OC DO NOT STEAL",          "mock/transistra.png"),
        new(Id: 4, CommentCount: 0, Points: 0, "Micko Mouse",          "mock/micko_mouse.jpg"),
        new(Id: 5, CommentCount: 0, Points: 0, "Garfield?",          "mock/kot_chyba.png")
    ];

    public void OnGet()
    {
    }
}