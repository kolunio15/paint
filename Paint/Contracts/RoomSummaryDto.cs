namespace Paint.Contracts;

public sealed record RoomSummaryDto(
    string Id,
    string Name,
    int UserCount,
    int MaxUsers,
    int PixelWidth,
    int PixelHeight,
    bool IsProtected);
