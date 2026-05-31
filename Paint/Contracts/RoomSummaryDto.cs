namespace Paint.Contracts;

public sealed record RoomSummaryDto(
    int Id,
    string Name,
    int UserCount,
    int MaxUsers,
    bool IsProtected);
