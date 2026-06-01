using Paint.Contracts;
using Paint.Models;

namespace Paint.Services;

public interface IRoomRepository
{
    Task<IReadOnlyList<RoomSummaryDto>> GetRoomsAsync(
        IReadOnlyDictionary<int, int> activeUserCounts,
        CancellationToken cancellationToken = default);

    Task<RoomSummaryDto?> GetRoomAsync(
        int roomId,
        int activeUserCount,
        CancellationToken cancellationToken = default);

    Task<bool> RoomExistsAsync(int roomId, CancellationToken cancellationToken = default);

    Task<Room> CreateRoomAsync(
        CreateRoomRequest request,
        string ownerId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRoomAsync(
        int roomId,
        string requestingUserId,
        CancellationToken cancellationToken = default);

    Task<RoomEventDto> AddRoomEventAsync(
        int roomId,
        string content,
        string userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoomEventDto>> GetRoomEventsAsync(
        int roomId,
        int startGlobalId,
        int? endGlobalId,
        CancellationToken cancellationToken = default);
}
