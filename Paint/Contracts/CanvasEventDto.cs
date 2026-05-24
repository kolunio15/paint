using System.Text.Json;

namespace Paint.Contracts;

public sealed record CanvasEventDto(
    long Id,
    string RoomId,
    string EventType,
    JsonElement Payload,
    string? UserName,
    DateTime CreatedAtUtc);
