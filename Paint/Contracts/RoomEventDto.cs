namespace Paint.Contracts;

public sealed record RoomEventDto(
    int GlobalEventId,
    int RoomId,
    string Content,
    string EventType,
    string UserId,
    DateTime CreatedAt);
