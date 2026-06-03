using Paint.Contracts;
using Paint.Models;

namespace Paint.Services;

public interface IRoomRepository
{
    Task<IReadOnlyList<RoomSummaryDto>> GetRoomsAsync(
        IReadOnlyDictionary<int, int> activeUserCounts,
        bool includeHidden = false,
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

    Task<(string? Path, int GlobalId)> GetRoomSnapshotAsync(
        int roomId,
        CancellationToken cancellationToken = default);

    // Flatten all events up to and including globalId into the given snapshot image
    // and delete them. Returns true if applied, false if ignored (stale/out of range).
    Task<bool> ApplySnapshotAsync(
        int roomId,
        string snapshotPath,
        int globalId,
        CancellationToken cancellationToken = default);

    // Remove all events + snapshot for a room (manual canvas clear). Returns the
    // previous snapshot file path (if any) so the caller can delete the file.
    Task<string?> ClearRoomEventsAsync(
        int roomId,
        CancellationToken cancellationToken = default);
}
