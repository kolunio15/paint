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
        [FromQuery] long? lastArtworkId,
        [FromQuery] int count = 20,
        CancellationToken cancellationToken = default)
    {
        var artworks = await _artworks.GetArtworksAsync(lastArtworkId, count, cancellationToken);
        return Ok(artworks);
    }

    [HttpGet("{artworkId:long}")]
    public async Task<ActionResult<ArtworkDetailsDto>> GetArtworkDetails(
        long artworkId,
        CancellationToken cancellationToken)
    {
        var artwork = await _artworks.GetArtworkDetailsAsync(artworkId, cancellationToken);
        return artwork is null ? NotFound() : Ok(artwork);
    }

    [Authorize]
    [HttpPost("{artworkId:long}/vote")]
    public async Task<IActionResult> RateArtwork(
        long artworkId,
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
    [HttpPost("{artworkId:long}/comments")]
    public async Task<ActionResult<CommentDto>> PostComment(
        long artworkId,
        [FromBody] PostCommentRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.Identity?.Name;
        var comment = await _artworks.PostCommentAsync(
            artworkId,
            userId,
            userName,
            request.Message,
            cancellationToken);

        return comment is null ? NotFound() : Created($"/api/artworks/{artworkId}/comments", comment);
    }

    [Authorize]
    [HttpPost("/api/rooms/{roomId}/publish")]
    public async Task<ActionResult<long>> PublishArtwork(
        string roomId,
        [FromBody] PublishArtworkRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
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

        var imageUrl = $"/uploads/artworks/{fileName}";
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.Identity?.Name;

        try
        {
            var artwork = await _artworks.PublishArtworkAsync(
                roomId,
                request.Title,
                imageUrl,
                imageUrl,
                userId,
                userName,
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
