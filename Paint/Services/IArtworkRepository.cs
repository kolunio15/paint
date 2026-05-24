using Paint.Contracts;
using Paint.Models;

namespace Paint.Services;

public interface IArtworkRepository
{
    Task<IReadOnlyList<ArtworkSummaryDto>> GetArtworksAsync(
        long? lastArtworkId,
        int count,
        CancellationToken cancellationToken = default);

    Task<ArtworkDetailsDto?> GetArtworkDetailsAsync(
        long artworkId,
        CancellationToken cancellationToken = default);

    Task<Artwork> PublishArtworkAsync(
        string roomId,
        string title,
        string imageUrl,
        string thumbnailUrl,
        string? userId,
        string? userName,
        CancellationToken cancellationToken = default);

    Task<CommentDto?> PostCommentAsync(
        long artworkId,
        string? userId,
        string? userName,
        string message,
        CancellationToken cancellationToken = default);

    Task<bool> RateArtworkAsync(
        long artworkId,
        string userId,
        VoteValue vote,
        CancellationToken cancellationToken = default);
}
