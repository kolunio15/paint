namespace Paint.Contracts;

public sealed record ChatMessageDto(
    string RoomId,
    string UserName,
    string Message,
    DateTime CreatedAtUtc);
