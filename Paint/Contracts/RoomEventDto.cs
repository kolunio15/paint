namespace Paint.Contracts;

public sealed record RoomEventDto(
    long GlobalEventId,
    string RoomId,
    string Content,
    string? UserName,
    DateTime CreatedAtUtc);
