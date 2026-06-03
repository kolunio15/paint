using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paint.Contracts;
using Paint.Data;
using Paint.Models;

namespace Paint.Services;

public class EfRoomRepository : IRoomRepository
{
    private readonly ApplicationDbContext _dbContext;
    private readonly PasswordHasher<Room> _passwordHasher = new();

    public EfRoomRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<RoomSummaryDto>> GetRoomsAsync(
        IReadOnlyDictionary<int, int> activeUserCounts,
        bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Room> query = _dbContext.Rooms.AsNoTracking();
        if (!includeHidden) query = query.Where(r => !r.IsHidden);
        var rooms = await query.OrderBy(r => r.Name).ToListAsync(cancellationToken);

        return rooms
            .Select(room =>
            {
                activeUserCounts.TryGetValue(room.Id, out var activeUserCount);
                return ToSummary(room, activeUserCount);
            })
            .ToList();
    }

    public async Task<RoomSummaryDto?> GetRoomAsync(
        int roomId,
        int activeUserCount,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == roomId, cancellationToken);

        return room is null ? null : ToSummary(room, activeUserCount);
    }

    public Task<bool> RoomExistsAsync(int roomId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken);
    }

    public async Task<Room> CreateRoomAsync(
        CreateRoomRequest request,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var room = new Room
        {
            Name = request.Name.Trim(),
            MaxUsers = request.MaxUsers,
            CanvasWidth = request.CanvasWidth,
            CanvasHeight = request.CanvasHeight,
            IsProtected = !string.IsNullOrWhiteSpace(request.Password),
            CreatedAt = DateTime.UtcNow,
            OwnerId = ownerId
        };

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            room.PasswordHash = _passwordHasher.HashPassword(room, request.Password);
        }

        room.Participants.Add(new RoomParticipant
        {
            Room = room,
            UserId = ownerId,
            JoinedAt = DateTime.UtcNow
        });

        _dbContext.Rooms.Add(room);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return room;
    }

    public async Task<bool> DeleteRoomAsync(
        int roomId,
        string requestingUserId,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .FirstOrDefaultAsync(candidate => candidate.Id == roomId, cancellationToken);

        if (room is null)
        {
            return false;
        }

        if (room.OwnerId != requestingUserId)
        {
            throw new UnauthorizedAccessException("Only room owner can delete this room.");
        }

        _dbContext.Rooms.Remove(room);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<RoomEventDto> AddRoomEventAsync(
        int roomId,
        string content,
        string userId,
        CancellationToken cancellationToken = default)
    {
        // globalId is a stable, contiguous per-room sequence that survives compaction:
        // base offset (SnapshotGlobalId) + number of live events still in the log.
        var snapshotGlobalId = await _dbContext.Rooms
            .AsNoTracking()
            .Where(room => room.Id == roomId)
            .Select(room => (int?)room.SnapshotGlobalId)
            .FirstOrDefaultAsync(cancellationToken) ?? -1;

        var liveCount = await _dbContext.CanvasEvents
            .CountAsync(canvasEvent => canvasEvent.RoomId == roomId, cancellationToken);

        var globalEventId = snapshotGlobalId + 1 + liveCount;

        var canvasEvent = new CanvasEvent
        {
            RoomId = roomId,
            UserId = userId,
            EventType = TryReadEventKind(content) ?? "event",
            Payload = string.IsNullOrWhiteSpace(content) ? "{}" : content,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.CanvasEvents.Add(canvasEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToEventDto(canvasEvent, globalEventId);
    }

    public async Task<IReadOnlyList<RoomEventDto>> GetRoomEventsAsync(
        int roomId,
        int startGlobalId,
        int? endGlobalId,
        CancellationToken cancellationToken = default)
    {
        var snapshotGlobalId = await _dbContext.Rooms
            .AsNoTracking()
            .Where(room => room.Id == roomId)
            .Select(room => (int?)room.SnapshotGlobalId)
            .FirstOrDefaultAsync(cancellationToken) ?? -1;

        var events = await _dbContext.CanvasEvents
            .AsNoTracking()
            .Where(canvasEvent => canvasEvent.RoomId == roomId)
            .OrderBy(canvasEvent => canvasEvent.Id)
            .ToListAsync(cancellationToken);

        return events
            .Select((canvasEvent, index) => ToEventDto(canvasEvent, snapshotGlobalId + 1 + index))
            .Where(canvasEvent =>
                canvasEvent.GlobalEventId >= startGlobalId &&
                (!endGlobalId.HasValue || canvasEvent.GlobalEventId < endGlobalId.Value))
            .ToList();
    }

    public async Task<(string? Path, int GlobalId)> GetRoomSnapshotAsync(
        int roomId,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .AsNoTracking()
            .Where(candidate => candidate.Id == roomId)
            .Select(candidate => new { candidate.SnapshotPath, candidate.SnapshotGlobalId })
            .FirstOrDefaultAsync(cancellationToken);

        return room is null ? (null, -1) : (room.SnapshotPath, room.SnapshotGlobalId);
    }

    public async Task<bool> ApplySnapshotAsync(
        int roomId,
        string snapshotPath,
        int globalId,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .FirstOrDefaultAsync(candidate => candidate.Id == roomId, cancellationToken);

        if (room is null)
        {
            return false;
        }

        var liveCount = await _dbContext.CanvasEvents
            .CountAsync(canvasEvent => canvasEvent.RoomId == roomId, cancellationToken);

        var newestGlobalId = room.SnapshotGlobalId + liveCount;

        // Ignore stale snapshots (already compacted past this point) or ones that
        // claim to cover events that don't exist yet.
        if (globalId <= room.SnapshotGlobalId || globalId > newestGlobalId)
        {
            return false;
        }

        var deleteCount = globalId - room.SnapshotGlobalId;
        var toDelete = await _dbContext.CanvasEvents
            .Where(canvasEvent => canvasEvent.RoomId == roomId)
            .OrderBy(canvasEvent => canvasEvent.Id)
            .Take(deleteCount)
            .ToListAsync(cancellationToken);

        _dbContext.CanvasEvents.RemoveRange(toDelete);
        room.SnapshotPath = snapshotPath;
        room.SnapshotGlobalId = globalId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<string?> ClearRoomEventsAsync(
        int roomId,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .FirstOrDefaultAsync(candidate => candidate.Id == roomId, cancellationToken);

        if (room is null)
        {
            return null;
        }

        var previousSnapshotPath = room.SnapshotPath;

        var events = await _dbContext.CanvasEvents
            .Where(canvasEvent => canvasEvent.RoomId == roomId)
            .ToListAsync(cancellationToken);

        _dbContext.CanvasEvents.RemoveRange(events);
        room.SnapshotPath = null;
        room.SnapshotGlobalId = -1;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return previousSnapshotPath;
    }

    private static RoomSummaryDto ToSummary(Room room, int activeUserCount)
    {
        return new RoomSummaryDto(
            room.Id,
            room.Name,
            activeUserCount,
            room.MaxUsers,
            room.CanvasWidth,
            room.CanvasHeight,
            room.IsProtected,
            room.IsHidden);
    }

    private static RoomEventDto ToEventDto(CanvasEvent canvasEvent, int globalEventId)
    {
        return new RoomEventDto(
            globalEventId,
            canvasEvent.RoomId,
            canvasEvent.Payload,
            canvasEvent.EventType,
            canvasEvent.UserId,
            canvasEvent.CreatedAt);
    }

    private static string? TryReadEventKind(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("kind", out var kindProperty)
                ? kindProperty.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
