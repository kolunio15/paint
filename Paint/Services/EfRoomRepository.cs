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
        CancellationToken cancellationToken = default)
    {
        var rooms = await _dbContext.Rooms
            .AsNoTracking()
            .OrderBy(room => room.Name)
            .ToListAsync(cancellationToken);

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

        return ToEventDto(canvasEvent);
    }

    public async Task<IReadOnlyList<RoomEventDto>> GetRoomEventsAsync(
        int roomId,
        int startGlobalId,
        int? endGlobalId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.CanvasEvents
            .AsNoTracking()
            .Where(canvasEvent => canvasEvent.RoomId == roomId && canvasEvent.Id >= startGlobalId);

        if (endGlobalId.HasValue)
        {
            query = query.Where(canvasEvent => canvasEvent.Id < endGlobalId.Value);
        }

        var events = await query
            .OrderBy(canvasEvent => canvasEvent.Id)
            .ToListAsync(cancellationToken);

        return events.Select(ToEventDto).ToList();
    }

    private static RoomSummaryDto ToSummary(Room room, int activeUserCount)
    {
        return new RoomSummaryDto(
            room.Id,
            room.Name,
            activeUserCount,
            room.MaxUsers,
            room.IsProtected);
    }

    private static RoomEventDto ToEventDto(CanvasEvent canvasEvent)
    {
        return new RoomEventDto(
            canvasEvent.Id,
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
