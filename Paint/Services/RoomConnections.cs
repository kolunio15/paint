using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Paint.Contracts;

namespace Paint.Services;

public class RoomConnections
{
    private readonly ConcurrentDictionary<string, ActiveRoom> _rooms = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoomConnections> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public RoomConnections(IServiceScopeFactory scopeFactory, ILogger<RoomConnections> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, int> GetActiveUserCounts()
    {
        return _rooms.ToDictionary(pair => pair.Key, pair => pair.Value.UserCount);
    }

    public int GetUserCount(string roomId)
    {
        return _rooms.TryGetValue(roomId, out var room) ? room.UserCount : 0;
    }

    public async Task HandleConnectionAsync(
        string roomId,
        WebSocket socket,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = user.Identity?.Name ?? "Guest";
        var client = new RoomConnectionClient(connectionId, userName, socket);
        var room = _rooms.GetOrAdd(roomId, static id => new ActiveRoom(id));

        room.AddConnection(client);

        try
        {
            await client.SendAsync(
                new
                {
                    type = "connected",
                    roomId,
                    connectionId,
                    canvas = await LoadCanvasSizeAsync(roomId, cancellationToken)
                },
                _jsonOptions,
                cancellationToken);

            await BroadcastPresenceAsync(room, cancellationToken);

            await ReceiveLoopAsync(room, client, userId, userName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Request ended normally.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected error in room {RoomId} for connection {ConnectionId}.", roomId, connectionId);
            await SendErrorAsync(client, "Connection error. Please reconnect.", CancellationToken.None);
        }
        finally
        {
            if (room.RemoveConnection(connectionId, out var removedClient))
            {
                removedClient.Dispose();
            }

            if (room.UserCount == 0)
            {
                _rooms.TryRemove(roomId, out _);
            }
            else
            {
                await BroadcastPresenceAsync(room, CancellationToken.None);
            }

            await CloseSocketQuietlyAsync(socket);
        }
    }

    private async Task ReceiveLoopAsync(
        ActiveRoom room,
        RoomConnectionClient client,
        string? userId,
        string userName,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
        {
            var rawMessage = await ReceiveTextMessageAsync(client.Socket, cancellationToken);
            if (rawMessage is null)
            {
                break;
            }

            await ProcessClientMessageAsync(room, client, userId, userName, rawMessage, cancellationToken);
        }
    }

    private async Task ProcessClientMessageAsync(
        ActiveRoom room,
        RoomConnectionClient client,
        string? userId,
        string userName,
        string rawMessage,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawMessage);
        }
        catch (JsonException)
        {
            await SendErrorAsync(client, "Invalid JSON message.", cancellationToken);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await SendErrorAsync(client, "Message must be a JSON object.", cancellationToken);
                return;
            }

            var messageType = TryGetString(root, "type") ?? TryGetString(root, "command");
            var kind = TryGetString(root, "kind");

            if (messageType == "broadcast" || kind == "brushPreview")
            {
                await HandleBroadcastAsync(room, client, root, cancellationToken);
                return;
            }

            if (messageType is null && kind is not null)
            {
                await HandleCanonicalEventAsync(room, client, userId, userName, root, cancellationToken);
                return;
            }

            if (messageType is null)
            {
                await SendErrorAsync(client, "Missing message type.", cancellationToken);
                return;
            }

            switch (messageType)
            {
                case "event":
                case "canvas-event":
                    await HandleCanonicalEventAsync(room, client, userId, userName, root, cancellationToken);
                    break;
                case "get_events":
                    await HandleGetEventsAsync(room, client, root, cancellationToken);
                    break;
                case "chat-message":
                    await HandleChatMessageAsync(room, userName, root, cancellationToken);
                    break;
                default:
                    await SendErrorAsync(client, $"Unsupported message type: {messageType}", cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleBroadcastAsync(
        ActiveRoom room,
        RoomConnectionClient client,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var content = TryGetElement(root, "content")
            ?? TryGetElement(root, "payload")
            ?? root;

        await room.BroadcastAsync(
            content,
            _jsonOptions,
            exceptConnectionId: client.ConnectionId,
            cancellationToken: cancellationToken);
    }

    private async Task HandleCanonicalEventAsync(
        ActiveRoom room,
        RoomConnectionClient client,
        string? userId,
        string userName,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var content = TryGetElement(root, "content")
            ?? TryGetElement(root, "event")
            ?? TryGetElement(root, "payload")
            ?? root;

        try
        {
            var savedEvent = await SaveRoomEventAsync(
                room.RoomId,
                content.GetRawText(),
                userId,
                userName,
                TryGetInt(root, "globalEventId"),
                cancellationToken);

            await room.BroadcastAsync(
                new
                {
                    type = "new_version",
                    last_global_id = savedEvent.GlobalEventId,
                    lastGlobalId = savedEvent.GlobalEventId
                },
                _jsonOptions,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not save canvas event in room {RoomId}.", room.RoomId);
            await SendErrorAsync(client, "Could not save canvas event.", cancellationToken);
        }
    }

    private async Task HandleGetEventsAsync(
        ActiveRoom room,
        RoomConnectionClient client,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var startGlobalId = TryGetLong(root, "startGlobalId") ?? TryGetLong(root, "start_global_id") ?? 0;
        var endGlobalId = TryGetLong(root, "endGlobalId") ?? TryGetLong(root, "end_global_id");
        var events = await LoadRoomEventsAsync(room.RoomId, startGlobalId, endGlobalId, cancellationToken);

        await client.SendAsync(
            new
            {
                type = "events",
                roomId = room.RoomId,
                events
            },
            _jsonOptions,
            cancellationToken);
    }

    private async Task HandleChatMessageAsync(
        ActiveRoom room,
        string userName,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var message = root.TryGetProperty("message", out var messageProperty)
            ? messageProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var dto = new ChatMessageDto(room.RoomId, userName, message.Trim(), DateTime.UtcNow);
        await room.BroadcastAsync(
            new { type = "chat-message", data = dto },
            _jsonOptions,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<CanvasEventDto>> LoadCanvasEventsAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomRepository>();
        return await rooms.GetCanvasEventsAsync(roomId, cancellationToken);
    }

    private async Task<IReadOnlyList<RoomEventDto>> LoadRoomEventsAsync(
        string roomId,
        long startGlobalId,
        long? endGlobalId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomRepository>();
        return await rooms.GetRoomEventsAsync(roomId, startGlobalId, endGlobalId, cancellationToken);
    }

    private async Task<RoomEventDto> SaveRoomEventAsync(
        string roomId,
        string content,
        string? userId,
        string? userName,
        int? clientGlobalEventId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomRepository>();
        return await rooms.AddRoomEventAsync(roomId, content, userId, userName, clientGlobalEventId, cancellationToken);
    }

    private async Task<object> LoadCanvasSizeAsync(string roomId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomRepository>();
        var room = await rooms.GetRoomAsync(roomId, GetUserCount(roomId), cancellationToken);

        return new
        {
            width = room?.PixelWidth ?? 1024,
            height = room?.PixelHeight ?? 768
        };
    }

    private async Task BroadcastPresenceAsync(ActiveRoom room, CancellationToken cancellationToken)
    {
        await room.BroadcastAsync(
            new
            {
                type = "room-presence",
                roomId = room.RoomId,
                userCount = room.UserCount
            },
            _jsonOptions,
            cancellationToken: cancellationToken);
    }

    private async Task SendErrorAsync(
        RoomConnectionClient client,
        string message,
        CancellationToken cancellationToken)
    {
        await client.SendAsync(new { type = "error", message }, _jsonOptions, cancellationToken);
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static JsonElement? TryGetElement(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) ? value.Clone() : null;
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static int? TryGetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return null;
    }

    private static long? TryGetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        return null;
    }

    private static async Task CloseSocketQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
