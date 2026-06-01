using Paint.Contracts;
using Paint.Models;

namespace Paint.Services;

public interface IArtworkRepository
{
    Task<IReadOnlyList<ArtworkSummaryDto>> GetArtworksAsync(
        int? lastArtworkId,
        int count,
        CancellationToken cancellationToken = default);

    Task<ArtworkDetailsDto?> GetArtworkDetailsAsync(
        int artworkId,
        CancellationToken cancellationToken = default);

    Task<Artwork> PublishArtworkAsync(
        int roomId,
        string title,
        string imageUrl,
        string authorId,
        CancellationToken cancellationToken = default);

    Task<CommentDto?> PostCommentAsync(
        int artworkId,
        string authorId,
        string message,
        CancellationToken cancellationToken = default);

    Task<bool> RateArtworkAsync(
        int artworkId,
        string userId,
        VoteKind vote,
        CancellationToken cancellationToken = default);
}
