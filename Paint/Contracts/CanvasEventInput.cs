using System.Text.Json;

namespace Paint.Contracts;

public sealed record CanvasEventInput(string EventType, JsonElement Payload);
