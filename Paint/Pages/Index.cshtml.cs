using Microsoft.AspNetCore.Mvc.RazorPages;
using Paint.Contracts;
using Paint.Services;

namespace Paint.Pages;

public class IndexModel : PageModel
{
    private readonly IArtworkRepository _artworks;

    public IndexModel(IArtworkRepository artworks)
    {
        _artworks = artworks;
    }

    public IReadOnlyList<ArtworkSummaryDto> Artworks { get; set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Artworks = await _artworks.GetArtworksAsync(null, 20, cancellationToken);
    }
}
