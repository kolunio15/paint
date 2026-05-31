using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Paint.Contracts;
using Paint.Services;

namespace Paint.Controllers;

[ApiController]
[Route("api/artworks")]
public class ArtworksController : ControllerBase
{
    private readonly IArtworkRepository _artworks;
    private readonly IWebHostEnvironment _environment;

    public ArtworksController(IArtworkRepository artworks, IWebHostEnvironment environment)
    {
        _artworks = artworks;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtworkSummaryDto>>> GetArtworks(
        [FromQuery] int? lastArtworkId,
        [FromQuery] int count = 20,
        CancellationToken cancellationToken = default)
    {
        var artworks = await _artworks.GetArtworksAsync(lastArtworkId, count, cancellationToken);
        return Ok(artworks);
    }

    [HttpGet("{artworkId:int}")]
    public async Task<ActionResult<ArtworkDetailsDto>> GetArtworkDetails(
        int artworkId,
        CancellationToken cancellationToken)
    {
        var artwork = await _artworks.GetArtworkDetailsAsync(artworkId, cancellationToken);
        return artwork is null ? NotFound() : Ok(artwork);
    }

    [Authorize]
    [HttpPost("{artworkId:int}/vote")]
    public async Task<IActionResult> RateArtwork(
        int artworkId,
        [FromBody] RateArtworkRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var saved = await _artworks.RateArtworkAsync(artworkId, userId, request.Vote, cancellationToken);
        return saved ? NoContent() : NotFound();
    }

    [Authorize]
    [HttpPost("{artworkId:int}/comments")]
    public async Task<ActionResult<CommentDto>> PostComment(
        int artworkId,
        [FromBody] PostCommentRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var comment = await _artworks.PostCommentAsync(
            artworkId,
            userId,
            request.Message,
            cancellationToken);

        return comment is null ? NotFound() : Created($"/api/artworks/{artworkId}/comments", comment);
    }

    [Authorize]
    [HttpPost("/api/rooms/{roomId:int}/publish")]
    public async Task<ActionResult<int>> PublishArtwork(
        int roomId,
        [FromBody] PublishArtworkRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(RemoveDataUriPrefix(request.ImageBase64));
        }
        catch (FormatException)
        {
            ModelState.AddModelError(nameof(request.ImageBase64), "ImageBase64 is not valid base64.");
            return ValidationProblem(ModelState);
        }

        var uploadRoot = Path.Combine(_environment.WebRootPath, "uploads", "artworks");
        Directory.CreateDirectory(uploadRoot);

        var fileName = $"{Guid.NewGuid():N}.png";
        var filePath = Path.Combine(uploadRoot, fileName);
        await System.IO.File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken);

        try
        {
            var artwork = await _artworks.PublishArtworkAsync(
                roomId,
                request.Title,
                $"/uploads/artworks/{fileName}",
                userId,
                cancellationToken);

            return Created($"/api/artworks/{artwork.Id}", artwork.Id);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    private static string RemoveDataUriPrefix(string value)
    {
        var commaIndex = value.IndexOf(',');
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0
            ? value[(commaIndex + 1)..]
            : value;
    }
}
