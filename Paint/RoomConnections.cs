using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Paint.Contracts;
using Paint.Services;

namespace Paint;

public class RoomConnections
{
    private readonly ConcurrentDictionary<string, ActiveRoom> _activeRooms = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoomConnections> _logger;

    public RoomConnections(IServiceScopeFactory scopeFactory, ILogger<RoomConnections> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public IReadOnlyDictionary<int, int> GetActiveUserCounts()
    {
        return _activeRooms
            .Where(pair => int.TryParse(pair.Key, out _))
            .ToDictionary(pair => int.Parse(pair.Key), pair => pair.Value.UserCount);
    }

    public async Task HandleConnection(
        string roomId,
        WebSocket socket,
        string userName,
        string userId,
        int canvasWidth,
        int canvasHeight,
        CancellationToken cancellationToken = default)
    {
        var room = _activeRooms.GetOrAdd(roomId, id => new ActiveRoom(this, id));
        await room.Post(new Connected(socket, canvasWidth, canvasHeight));

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(socket, cancellationToken);
                if (message is null)
                {
                    break;
                }

                _logger.LogInformation("Received room message: room={RoomId} message='{Message}'", roomId, message);

                if (message.StartsWith("msg ", StringComparison.Ordinal))
                {
                    await room.Post(new ChatMessage(userName, message["msg ".Length..]));
                }
                else if (message.StartsWith("event ", StringComparison.Ordinal))
                {
                    await room.Post(new CanvasEvent(socket, message["event ".Length..], userId));
                }
                else if (message.StartsWith("get_events ", StringComparison.Ordinal))
                {
                    if (TryParseRange(message["get_events ".Length..], out var startId, out var endId))
                    {
                        await room.Post(new GetEvents(socket, startId, endId));
                    }
                    else
                    {
                        await room.Post(new ProtocolError(socket, "invalid event range"));
                    }
                }
                else if (message.StartsWith("broadcast ", StringComparison.Ordinal))
                {
                    await room.Post(new Broadcast(message["broadcast ".Length..]));
                }
                else
                {
                    await room.Post(new ProtocolError(socket, "unsupported message"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            _logger.LogInformation(exception, "WebSocket connection closed unexpectedly: room={RoomId}", roomId);
        }
        finally
        {
            await room.Post(new Disconnected(socket));
        }
    }

    private async Task<RoomEventDto> SaveRoomEventAsync(
        string roomId,
        string content,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(roomId, out var databaseRoomId))
        {
            throw new InvalidOperationException($"Room id '{roomId}' is not a database room id.");
        }

        using var scope = _scopeFactory.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomRepository>();
        return await rooms.AddRoomEventAsync(databaseRoomId, content, userId, cancellationToken);
    }

    private async Task<IReadOnlyList<RoomEventDto>> LoadRoomEventsAsync(
        string roomId,
        int startId,
        int? endId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(roomId, out var databaseRoomId))
        {
            return [];
        }

        using var scope = _scopeFactory.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomRepository>();
        return await rooms.GetRoomEventsAsync(databaseRoomId, startId, endId, cancellationToken);
    }

    private void RemoveRoomIfSame(string roomId, ActiveRoom room)
    {
        if (_activeRooms.TryGetValue(roomId, out var currentRoom) && ReferenceEquals(currentRoom, room))
        {
            _activeRooms.TryRemove(roomId, out _);
        }
    }

    private static bool TryParseRange(string input, out int startId, out int? endId)
    {
        startId = 0;
        endId = null;

        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out startId))
        {
            return false;
        }

        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], out var parsedEndId))
            {
                return false;
            }

            endId = parsedEndId;
        }

        return true;
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, CancellationToken cancellationToken)
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

    private static async Task<bool> SendTextAsync(
        WebSocket socket,
        string message,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return false;
        }

        try
        {
            var data = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private abstract record Message;
    private sealed record Connected(WebSocket Socket, int CanvasWidth, int CanvasHeight) : Message;
    private sealed record Disconnected(WebSocket Socket) : Message;
    private sealed record ChatMessage(string UserName, string Text) : Message;
    private sealed record CanvasEvent(WebSocket Source, string Content, string UserId) : Message;
    private sealed record GetEvents(WebSocket Connection, int StartId, int? EndId) : Message;
    private sealed record Broadcast(string Content) : Message;
    private sealed record ProtocolError(WebSocket Connection, string Text) : Message;

    private sealed class ActiveRoom
    {
        private readonly RoomConnections _roomConnections;
        private readonly string _roomId;
        private readonly Channel<Message> _backgroundChannel = Channel.CreateUnbounded<Message>();
        private readonly Dictionary<WebSocket, int> _connectedSockets = [];
        private int _nextConnectionId;
        private int _userCount;

        public ActiveRoom(RoomConnections roomConnections, string roomId)
        {
            _roomConnections = roomConnections;
            _roomId = roomId;
            _ = ProcessMessagesAsync();
        }

        public int UserCount => Volatile.Read(ref _userCount);

        public ValueTask Post(Message message)
        {
            return _backgroundChannel.Writer.WriteAsync(message);
        }

        private async Task ProcessMessagesAsync()
        {
            await foreach (var message in _backgroundChannel.Reader.ReadAllAsync())
            {
                switch (message)
                {
                    case Connected(var socket, var canvasWidth, var canvasHeight):
                        await HandleConnectedAsync(socket, canvasWidth, canvasHeight);
                        break;
                    case Disconnected(var socket):
                        await HandleDisconnectedAsync(socket);
                        if (_connectedSockets.Count == 0)
                        {
                            _roomConnections._logger.LogInformation("Room empty: room={RoomId}", _roomId);
                            _roomConnections.RemoveRoomIfSame(_roomId, this);
                            return;
                        }
                        break;
                    case ChatMessage(var userName, var text):
                        await BroadcastAsync($"msg {userName}: {text}");
                        break;
                    case CanvasEvent(var source, var content, var userId):
                        await HandleCanvasEventAsync(source, content, userId);
                        break;
                    case GetEvents(var connection, var startId, var endId):
                        await HandleGetEventsAsync(connection, startId, endId);
                        break;
                    case Broadcast(var content):
                        await BroadcastAsync($"broadcast {content}");
                        break;
                    case ProtocolError(var connection, var text):
                        await SendTextAsync(connection, $"error {text}", CancellationToken.None);
                        break;
                }
            }
        }

        private async Task HandleConnectedAsync(WebSocket socket, int canvasWidth, int canvasHeight)
        {
            var connectionId = _nextConnectionId++;
            _connectedSockets[socket] = connectionId;
            Volatile.Write(ref _userCount, _connectedSockets.Count);
            _roomConnections._logger.LogInformation(
                "New connection: room={RoomId} connectionId={ConnectionId}",
                _roomId,
                connectionId);

            var assigned = await SendTextAsync(socket, $"assigned_id {connectionId}", CancellationToken.None);
            var sized = assigned && await SendTextAsync(socket, $"canvas_size {canvasWidth} {canvasHeight}", CancellationToken.None);
            if (!sized)
            {
                _connectedSockets.Remove(socket);
                Volatile.Write(ref _userCount, _connectedSockets.Count);
            }
        }

        private async Task HandleDisconnectedAsync(WebSocket socket)
        {
            if (!_connectedSockets.Remove(socket, out var connectionId))
            {
                return;
            }

            Volatile.Write(ref _userCount, _connectedSockets.Count);
            _roomConnections._logger.LogInformation(
                "Connection ended: room={RoomId} connectionId={ConnectionId}",
                _roomId,
                connectionId);

            await BroadcastAsync($"disconnect {connectionId}");
        }

        private async Task HandleCanvasEventAsync(WebSocket source, string content, string userId)
        {
            try
            {
                var savedEvent = await _roomConnections.SaveRoomEventAsync(
                    _roomId,
                    content,
                    userId,
                    CancellationToken.None);

                await BroadcastAsync($"new_version {savedEvent.GlobalEventId}");
            }
            catch (Exception exception)
            {
                _roomConnections._logger.LogWarning(
                    exception,
                    "Could not save canvas event: room={RoomId}",
                    _roomId);
                await SendTextAsync(source, "error event save failed", CancellationToken.None);
            }
        }

        private async Task HandleGetEventsAsync(WebSocket connection, int startId, int? endId)
        {
            try
            {
                var events = await _roomConnections.LoadRoomEventsAsync(
                    _roomId,
                    startId,
                    endId,
                    CancellationToken.None);

                foreach (var canvasEvent in events)
                {
                    var sent = await SendTextAsync(
                        connection,
                        $"event {canvasEvent.GlobalEventId} {canvasEvent.Content}",
                        CancellationToken.None);

                    if (!sent)
                    {
                        _connectedSockets.Remove(connection);
                        Volatile.Write(ref _userCount, _connectedSockets.Count);
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                _roomConnections._logger.LogWarning(
                    exception,
                    "Could not load canvas events: room={RoomId}",
                    _roomId);
                await SendTextAsync(connection, "error event load failed", CancellationToken.None);
            }
        }

        private async Task BroadcastAsync(string message)
        {
            var failedSockets = new List<WebSocket>();
            foreach (var socket in _connectedSockets.Keys.ToList())
            {
                var sent = await SendTextAsync(socket, message, CancellationToken.None);
                if (!sent)
                {
                    failedSockets.Add(socket);
                }
            }

            foreach (var socket in failedSockets)
            {
                _connectedSockets.Remove(socket);
            }

            if (failedSockets.Count > 0)
            {
                Volatile.Write(ref _userCount, _connectedSockets.Count);
            }
        }
    }
}
