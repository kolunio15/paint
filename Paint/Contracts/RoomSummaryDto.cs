namespace Paint.Contracts;

public sealed record RoomSummaryDto(
    int Id,
    string Name,
    int UserCount,
    int MaxUsers,
    int CanvasWidth,
    int CanvasHeight,
    bool IsProtected,
    bool IsHidden = false);
