using Microsoft.EntityFrameworkCore;
using Paint.Contracts;
using Paint.Data;
using Paint.Models;

namespace Paint.Services;

public class EfArtworkRepository : IArtworkRepository
{
    private readonly ApplicationDbContext _dbContext;

    public EfArtworkRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ArtworkSummaryDto>> GetArtworksAsync(
        long? lastArtworkId,
        int count,
        CancellationToken cancellationToken = default)
    {
        var safeCount = Math.Clamp(count, 1, 100);
        var query = _dbContext.Artworks.AsNoTracking();

        if (lastArtworkId.HasValue)
        {
            var cursor = await _dbContext.Artworks
                .AsNoTracking()
                .FirstOrDefaultAsync(artwork => artwork.Id == lastArtworkId.Value, cancellationToken);

            if (cursor is not null)
            {
                query = query.Where(artwork =>
                    artwork.CreatedAtUtc < cursor.CreatedAtUtc ||
                    (artwork.CreatedAtUtc == cursor.CreatedAtUtc && artwork.Id < cursor.Id));
            }
        }

        var artworks = await query
            .OrderByDescending(artwork => artwork.CreatedAtUtc)
            .ThenByDescending(artwork => artwork.Id)
            .Take(safeCount)
            .ToListAsync(cancellationToken);

        return artworks
            .Select(artwork => new ArtworkSummaryDto(artwork.Id, artwork.Title, artwork.ThumbnailUrl))
            .ToList();
    }

    public async Task<ArtworkDetailsDto?> GetArtworkDetailsAsync(
        long artworkId,
        CancellationToken cancellationToken = default)
    {
        var artwork = await _dbContext.Artworks
            .AsNoTracking()
            .Include(candidate => candidate.Comments)
            .Include(candidate => candidate.Votes)
            .FirstOrDefaultAsync(candidate => candidate.Id == artworkId, cancellationToken);

        if (artwork is null)
        {
            return null;
        }

        var users = new List<UserDto>();
        if (!string.IsNullOrWhiteSpace(artwork.CreatedByUserId) ||
            !string.IsNullOrWhiteSpace(artwork.CreatedByUserName))
        {
            users.Add(new UserDto(artwork.CreatedByUserId, artwork.CreatedByUserName ?? "Unknown"));
        }

        var comments = artwork.Comments
            .OrderBy(comment => comment.CreatedAtUtc)
            .Select(comment => new CommentDto(
                new UserDto(comment.UserId, comment.UserName),
                comment.Content,
                comment.CreatedAtUtc))
            .ToList();

        var score = artwork.Votes.Sum(vote => (int)vote.Vote);

        return new ArtworkDetailsDto(
            artwork.Id,
            artwork.Title,
            artwork.ImageUrl,
            users,
            comments,
            score);
    }

    public async Task<Artwork> PublishArtworkAsync(
        string roomId,
        string title,
        string imageUrl,
        string thumbnailUrl,
        string? userId,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (!await _dbContext.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken))
        {
            throw new InvalidOperationException($"Room '{roomId}' does not exist.");
        }

        var artwork = new Artwork
        {
            RoomId = roomId,
            Title = title.Trim(),
            ImageUrl = imageUrl,
            ThumbnailUrl = thumbnailUrl,
            CreatedByUserId = userId,
            CreatedByUserName = userName,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Artworks.Add(artwork);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return artwork;
    }

    public async Task<CommentDto?> PostCommentAsync(
        long artworkId,
        string? userId,
        string? userName,
        string message,
        CancellationToken cancellationToken = default)
    {
        var exists = await _dbContext.Artworks
            .AnyAsync(artwork => artwork.Id == artworkId, cancellationToken);

        if (!exists)
        {
            return null;
        }

        var comment = new ArtworkComment
        {
            ArtworkId = artworkId,
            UserId = userId,
            UserName = userName ?? "Unknown",
            Content = message.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ArtworkComments.Add(comment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CommentDto(
            new UserDto(comment.UserId, comment.UserName),
            comment.Content,
            comment.CreatedAtUtc);
    }

    public async Task<bool> RateArtworkAsync(
        long artworkId,
        string userId,
        VoteValue vote,
        CancellationToken cancellationToken = default)
    {
        var exists = await _dbContext.Artworks
            .AnyAsync(artwork => artwork.Id == artworkId, cancellationToken);

        if (!exists)
        {
            return false;
        }

        var existingVote = await _dbContext.ArtworkVotes
            .FirstOrDefaultAsync(
                candidate => candidate.ArtworkId == artworkId && candidate.UserId == userId,
                cancellationToken);

        if (vote == VoteValue.Neutral)
        {
            if (existingVote is not null)
            {
                _dbContext.ArtworkVotes.Remove(existingVote);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        if (existingVote is null)
        {
            existingVote = new ArtworkVote
            {
                ArtworkId = artworkId,
                UserId = userId
            };
            _dbContext.ArtworkVotes.Add(existingVote);
        }

        existingVote.Vote = vote;
        existingVote.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
