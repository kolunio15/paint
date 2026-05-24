using System.Globalization;
using System.Text;
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
    private readonly PasswordHasher<PaintRoom> _roomPasswordHasher = new();

    public EfRoomRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<RoomSummaryDto>> GetRoomsAsync(
        IReadOnlyDictionary<string, int> activeUserCounts,
        CancellationToken cancellationToken = default)
    {
        var rooms = await _dbContext.Rooms
            .AsNoTracking()
            .OrderBy(room => room.Name)
            .ToListAsync(cancellationToken);

        return rooms
            .Select(room => ToSummary(room, activeUserCounts.GetValueOrDefault(room.Id)))
            .ToList();
    }

    public async Task<RoomSummaryDto?> GetRoomAsync(
        string roomId,
        int activeUserCount,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == roomId, cancellationToken);

        return room is null ? null : ToSummary(room, activeUserCount);
    }

    public Task<bool> RoomExistsAsync(string roomId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken);
    }

    public async Task<PaintRoom> CreateRoomAsync(
        CreateRoomRequest request,
        string? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var room = new PaintRoom
        {
            Id = await CreateUniqueSlugAsync(request.Name, cancellationToken),
            Name = request.Name.Trim(),
            MaxUsers = request.MaxUsers,
            PixelWidth = request.PixelWidth,
            PixelHeight = request.PixelHeight,
            IsProtected = request.IsProtected || !string.IsNullOrWhiteSpace(request.Password),
            CreatedByUserId = createdByUserId,
            CreatedAtUtc = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            room.PasswordHash = _roomPasswordHasher.HashPassword(room, request.Password);
        }

        _dbContext.Rooms.Add(room);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return room;
    }

    public async Task<IReadOnlyList<CanvasEventDto>> GetCanvasEventsAsync(
        string roomId,
        CancellationToken cancellationToken = default)
    {
        var events = await _dbContext.CanvasEvents
            .AsNoTracking()
            .Where(canvasEvent => canvasEvent.RoomId == roomId)
            .OrderBy(canvasEvent => canvasEvent.Id)
            .ToListAsync(cancellationToken);

        return events.Select(ToDto).ToList();
    }

    public async Task<CanvasEventDto> AddCanvasEventAsync(
        string roomId,
        CanvasEventInput input,
        string? userId,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        var eventType = input.EventType.Trim();
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new InvalidOperationException("Canvas event type cannot be empty.");
        }

        var canvasEvent = new CanvasEvent
        {
            RoomId = roomId,
            EventType = eventType,
            PayloadJson = input.Payload.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : input.Payload.GetRawText(),
            UserId = userId,
            UserName = userName,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.CanvasEvents.Add(canvasEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(canvasEvent);
    }

    public async Task<IReadOnlyList<RoomEventDto>> GetRoomEventsAsync(
        string roomId,
        long startGlobalId,
        long? endGlobalId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.CanvasEvents
            .AsNoTracking()
            .Where(canvasEvent => canvasEvent.RoomId == roomId && canvasEvent.Id >= startGlobalId);

        if (endGlobalId.HasValue)
        {
            query = query.Where(canvasEvent => canvasEvent.Id <= endGlobalId.Value);
        }

        var events = await query
            .OrderBy(canvasEvent => canvasEvent.Id)
            .ToListAsync(cancellationToken);

        return events.Select(ToRoomEventDto).ToList();
    }

    public async Task<RoomEventDto> AddRoomEventAsync(
        string roomId,
        string content,
        string? userId,
        string? userName,
        int? clientGlobalEventId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await RoomExistsAsync(roomId, cancellationToken))
        {
            throw new InvalidOperationException($"Room '{roomId}' does not exist.");
        }

        var canvasEvent = new CanvasEvent
        {
            RoomId = roomId,
            EventType = TryReadEventKind(content) ?? "event",
            ClientGlobalEventId = clientGlobalEventId,
            PayloadJson = string.IsNullOrWhiteSpace(content) ? "{}" : content,
            UserId = userId,
            UserName = userName,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.CanvasEvents.Add(canvasEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToRoomEventDto(canvasEvent);
    }

    public async Task<bool> DeleteRoomAsync(
        string roomId,
        string? requestingUserId,
        CancellationToken cancellationToken = default)
    {
        var room = await _dbContext.Rooms
            .FirstOrDefaultAsync(candidate => candidate.Id == roomId, cancellationToken);

        if (room is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(room.CreatedByUserId) &&
            room.CreatedByUserId != requestingUserId)
        {
            throw new UnauthorizedAccessException("Only the room owner can delete the room.");
        }

        _dbContext.Rooms.Remove(room);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<string> CreateUniqueSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(name);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            baseSlug = $"room-{Guid.NewGuid():N}"[..13];
        }

        var slug = baseSlug;
        var suffix = 2;
        while (await _dbContext.Rooms.AnyAsync(room => room.Id == slug, cancellationToken))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    private static string Slugify(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var previousWasDash = false;

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasDash = false;
                continue;
            }

            if (!previousWasDash && builder.Length > 0)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static RoomSummaryDto ToSummary(PaintRoom room, int activeUserCount)
    {
        return new RoomSummaryDto(
            room.Id,
            room.Name,
            activeUserCount,
            room.MaxUsers,
            room.PixelWidth,
            room.PixelHeight,
            room.IsProtected);
    }

    private static CanvasEventDto ToDto(CanvasEvent canvasEvent)
    {
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(canvasEvent.PayloadJson)
                    ? "{}"
                    : canvasEvent.PayloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            payload = document.RootElement.Clone();
        }

        return new CanvasEventDto(
            canvasEvent.Id,
            canvasEvent.RoomId,
            canvasEvent.EventType,
            payload,
            canvasEvent.UserName,
            canvasEvent.CreatedAtUtc);
    }

    private static RoomEventDto ToRoomEventDto(CanvasEvent canvasEvent)
    {
        return new RoomEventDto(
            canvasEvent.Id,
            canvasEvent.RoomId,
            string.IsNullOrWhiteSpace(canvasEvent.PayloadJson) ? "{}" : canvasEvent.PayloadJson,
            canvasEvent.UserName,
            canvasEvent.CreatedAtUtc);
    }

    private static string? TryReadEventKind(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("kind", out var kindProperty))
            {
                return kindProperty.GetString();
            }

            if (document.RootElement.TryGetProperty("eventType", out var eventTypeProperty))
            {
                return eventTypeProperty.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
