using Paint.Contracts;
using Paint.Models;

namespace Paint.Services;

public interface IRoomRepository
{
    Task<IReadOnlyList<RoomSummaryDto>> GetRoomsAsync(
        IReadOnlyDictionary<string, int> activeUserCounts,
        CancellationToken cancellationToken = default);

    Task<RoomSummaryDto?> GetRoomAsync(
        string roomId,
        int activeUserCount,
        CancellationToken cancellationToken = default);

    Task<bool> RoomExistsAsync(string roomId, CancellationToken cancellationToken = default);

    Task<PaintRoom> CreateRoomAsync(
        CreateRoomRequest request,
        string? createdByUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CanvasEventDto>> GetCanvasEventsAsync(
        string roomId,
        CancellationToken cancellationToken = default);

    Task<CanvasEventDto> AddCanvasEventAsync(
        string roomId,
        CanvasEventInput input,
        string? userId,
        string? userName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoomEventDto>> GetRoomEventsAsync(
        string roomId,
        long startGlobalId,
        long? endGlobalId,
        CancellationToken cancellationToken = default);

    Task<RoomEventDto> AddRoomEventAsync(
        string roomId,
        string content,
        string? userId,
        string? userName,
        int? clientGlobalEventId = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRoomAsync(
        string roomId,
        string? requestingUserId,
        CancellationToken cancellationToken = default);
}
