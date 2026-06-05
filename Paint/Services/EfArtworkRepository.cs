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
        int? lastArtworkId,
        int count,
        bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        var safeCount = Math.Clamp(count, 1, 100);
        var query = _dbContext.Artworks.AsNoTracking();
        if (!includeHidden) query = query.Where(a => !a.IsHidden);

        if (lastArtworkId.HasValue)
        {
            var cursor = await _dbContext.Artworks
                .AsNoTracking()
                .FirstOrDefaultAsync(artwork => artwork.Id == lastArtworkId.Value, cancellationToken);

            if (cursor is not null)
            {
                query = query.Where(artwork =>
                    artwork.CreatedAt < cursor.CreatedAt ||
                    (artwork.CreatedAt == cursor.CreatedAt && artwork.Id < cursor.Id));
            }
        }

        var artworks = await query
            .Include(a => a.Votes)
            .OrderByDescending(artwork => artwork.CreatedAt)
            .ThenByDescending(artwork => artwork.Id)
            .Take(safeCount)
            .ToListAsync(cancellationToken);

        return artworks
            .Select(artwork => new ArtworkSummaryDto(
                artwork.Id,
                artwork.Title,
                artwork.ImageUrl,
                artwork.Votes.Sum(v => v.Value),
                artwork.IsHidden))
            .ToList();
    }

    public async Task<ArtworkDetailsDto?> GetArtworkDetailsAsync(
        int artworkId,
        bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        var artwork = await _dbContext.Artworks
            .AsNoTracking()
            .Include(candidate => candidate.Author)
            .Include(candidate => candidate.Comments)
                .ThenInclude(comment => comment.Author)
            .Include(candidate => candidate.Votes)
            .FirstOrDefaultAsync(candidate => candidate.Id == artworkId &&
                (includeHidden || !candidate.IsHidden), cancellationToken);

        if (artwork is null)
        {
            return null;
        }

        var author = ToUserDto(artwork.Author);
        var comments = artwork.Comments
            .Where(comment => includeHidden || !comment.IsHidden)
            .OrderBy(comment => comment.CreatedAt)
            .Select(comment => new CommentDto(
                comment.Id,
                ToUserDto(comment.Author),
                comment.Content,
                comment.CreatedAt))
            .ToList();

        var score = artwork.Votes.Sum(vote => vote.Value);

        return new ArtworkDetailsDto(
            artwork.Id,
            artwork.Title,
            artwork.ImageUrl,
            [author],
            comments,
            score,
            artwork.IsHidden);
    }

    public async Task<Artwork> PublishArtworkAsync(
        int roomId,
        string title,
        string imageUrl,
        string authorId,
        CancellationToken cancellationToken = default)
    {
        var roomExists = await _dbContext.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken);
        if (!roomExists)
        {
            throw new InvalidOperationException($"Room {roomId} does not exist.");
        }

        var artwork = new Artwork
        {
            Title = title.Trim(),
            ImageUrl = imageUrl,
            CreatedAt = DateTime.UtcNow,
            AuthorId = authorId
        };

        _dbContext.Artworks.Add(artwork);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return artwork;
    }

    public async Task<CommentDto?> PostCommentAsync(
        int artworkId,
        string authorId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var artworkExists = await _dbContext.Artworks.AnyAsync(
            artwork => artwork.Id == artworkId,
            cancellationToken);

        if (!artworkExists)
        {
            return null;
        }

        var comment = new Comment
        {
            ArtworkId = artworkId,
            AuthorId = authorId,
            Content = message.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Comments.Add(comment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var author = await _dbContext.Users
            .AsNoTracking()
            .FirstAsync(user => user.Id == authorId, cancellationToken);

        return new CommentDto(comment.Id, ToUserDto(author), comment.Content, comment.CreatedAt);
    }

    public async Task<bool> RateArtworkAsync(
        int artworkId,
        string userId,
        VoteKind vote,
        CancellationToken cancellationToken = default)
    {
        var artworkExists = await _dbContext.Artworks.AnyAsync(
            artwork => artwork.Id == artworkId,
            cancellationToken);

        if (!artworkExists)
        {
            return false;
        }

        var existingVote = await _dbContext.Votes.FirstOrDefaultAsync(
            candidate => candidate.ArtworkId == artworkId && candidate.UserId == userId,
            cancellationToken);

        if (vote == VoteKind.Neutral)
        {
            if (existingVote is not null)
            {
                _dbContext.Votes.Remove(existingVote);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        if (existingVote is null)
        {
            existingVote = new Vote
            {
                ArtworkId = artworkId,
                UserId = userId
            };
            _dbContext.Votes.Add(existingVote);
        }

        existingVote.Value = (int)vote;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static UserDto ToUserDto(ApplicationUser user)
    {
        return new UserDto(
            user.Id,
            user.DisplayName ?? user.UserName ?? user.Id);
    }
}
