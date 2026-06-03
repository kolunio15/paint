using Microsoft.EntityFrameworkCore;
using Paint.Data;

namespace Paint.Services;

public class ContentDeletionService(IServiceProvider services, IWebHostEnvironment env, ILogger<ContentDeletionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PerformDeletionAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Error during scheduled content deletion"); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task PerformDeletionAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;

        var artworks = await db.Artworks
            .Where(a => a.ScheduledDeletionAt.HasValue && a.ScheduledDeletionAt <= now)
            .ToListAsync(ct);

        foreach (var artwork in artworks)
        {
            if (!string.IsNullOrWhiteSpace(artwork.ImageUrl))
            {
                var filePath = Path.Combine(env.WebRootPath, artwork.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            db.Artworks.Remove(artwork);
            logger.LogInformation("Permanently deleted artwork {Id} ({Title})", artwork.Id, artwork.Title);
        }

        var comments = await db.Comments
            .Where(c => c.ScheduledDeletionAt.HasValue && c.ScheduledDeletionAt <= now)
            .ToListAsync(ct);
        db.Comments.RemoveRange(comments);
        if (comments.Count > 0)
            logger.LogInformation("Permanently deleted {Count} scheduled comment(s)", comments.Count);

        var rooms = await db.Rooms
            .Where(r => r.ScheduledDeletionAt.HasValue && r.ScheduledDeletionAt <= now)
            .ToListAsync(ct);

        foreach (var room in rooms)
        {
            DeleteRoomSnapshotFiles(room.Id);
        }

        db.Rooms.RemoveRange(rooms);
        if (rooms.Count > 0)
            logger.LogInformation("Permanently deleted {Count} scheduled room(s)", rooms.Count);

        if (artworks.Count == 0 && comments.Count == 0 && rooms.Count == 0)
            return;

        // Drop active queue items that point at content we just removed so the queue
        // doesn't keep dangling references. The ModerationActions audit log is preserved.
        var artworkIds = artworks.Select(a => a.Id).ToHashSet();
        var commentIds = comments.Select(c => c.Id).ToHashSet();
        var roomIds = rooms.Select(r => r.Id).ToHashSet();

        var staleQueueItems = await db.ModerationQueue
            .Where(q => (q.TargetArtworkId.HasValue && artworkIds.Contains(q.TargetArtworkId.Value))
                     || (q.TargetCommentId.HasValue && commentIds.Contains(q.TargetCommentId.Value))
                     || (q.TargetRoomId.HasValue && roomIds.Contains(q.TargetRoomId.Value)))
            .ToListAsync(ct);
        db.ModerationQueue.RemoveRange(staleQueueItems);

        await db.SaveChangesAsync(ct);
    }

    private void DeleteRoomSnapshotFiles(int roomId)
    {
        try
        {
            var snapshotDir = Path.Combine(env.WebRootPath, "uploads", "snapshots");
            if (!Directory.Exists(snapshotDir))
                return;

            foreach (var file in Directory.GetFiles(snapshotDir, $"room-{roomId}-*.png"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete snapshot files for room {RoomId}", roomId);
        }
    }
}
