using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Paint.Contracts;
using Paint.Services;

namespace Paint.Pages;

public class ArtworkModel : PageModel
{
    private readonly IArtworkRepository _artworks;

    public ArtworkModel(IArtworkRepository artworks)
    {
        _artworks = artworks;
    }

    public ArtworkDetailsDto Artwork { get; set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var artwork = await _artworks.GetArtworkDetailsAsync(id, cancellationToken);
        if (artwork is null) return NotFound();
        Artwork = artwork;
        return Page();
    }
}
