using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Paint;

class RoomConnections(ILogger<RoomConnections> logger)
{
    record Message;
    record Connected(WebSocket Socket) : Message;
    record Disconnected(WebSocket Socket) : Message;
    record ChatMessage(string UserName, string Text) : Message;
    record CanvasEvent(string Content) : Message;
    record GetEvents(WebSocket connection, int StartId, int? EndId) : Message;

    ILogger<RoomConnections> _logger = logger;
    class ActiveRoom
    {
        RoomConnections _roomConnections;
        string _roomId;

        Channel<Message> _backgroundChannel = Channel.CreateUnbounded<Message>();
       
        public ActiveRoom(RoomConnections connections, string roomId)
        {
            _roomConnections = connections;
            _roomId = roomId;
            _ = ProcessMessages();
        }

        public async ValueTask Post(Message m)
        {
            await _backgroundChannel.Writer.WriteAsync(m);
        }

        async Task ProcessMessages()
        {
            int nextConnectionId = 0;
            Dictionary<WebSocket, int> connectedSockets = [];

            int nextGlobalId = 0;
            List<(int GlobalId, string Content)> events = [];

            await foreach (var m in _backgroundChannel.Reader.ReadAllAsync())
            {
                switch (m)
                {
                    case Connected(var socket):
                    {
                        int connectionId = nextConnectionId++;
                        connectedSockets.Add(socket, connectionId);
                        _roomConnections._logger.LogInformation("new connection: room={0} connectionId={1}", _roomId, connectionId);

                        var data = Encoding.UTF8.GetBytes($"assigned_id {connectionId}");
                        await socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
                        break;
                    }
                    case Disconnected(var socket):
                    {
                        int connectionId = connectedSockets[socket];
                        _roomConnections._logger.LogInformation("connection ended: room={0} connectionId={1}", _roomId, connectionId);
                        connectedSockets.Remove(socket);

                        if (connectedSockets.Count == 0)
                        {
                            _roomConnections._logger.LogInformation("room empty: room={0}", _roomId);
                            lock (_roomConnections._activeRooms)
                            {
                                _roomConnections._activeRooms.Remove(_roomId);
                            }
                            return;
                        }
                        break;
                    }
                    case ChatMessage(string userName, string text):
                    {
                        var data = Encoding.UTF8.GetBytes($"msg {userName}: {text}");

                        var sendTasks = connectedSockets.Keys.Select(socket =>
                            socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None));

                        await Task.WhenAll(sendTasks);
                        break;
                    }
                    case CanvasEvent(string content):
                    {
                        int id = nextGlobalId++;
                        events.Add(new(id, content));

                        var data = Encoding.UTF8.GetBytes($"new_version {id}");

                        var sendTasks = connectedSockets.Keys.Select(socket =>
                            socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None));

                        await Task.WhenAll(sendTasks);
                        break;
                    }
                    case GetEvents(WebSocket connection, int startId, var endId):
                    {
                        foreach (var e in events)
                        {
                            if (e.GlobalId >= startId && (endId == null || e.GlobalId < endId.Value))
                            {
                                using var stream = WebSocketStream.CreateWritableMessageStream(connection, WebSocketMessageType.Text);
                                using var writer = new StreamWriter(stream);
                                await writer.WriteAsync($"event {e.GlobalId} {e.Content}");
                            }
                        }
                        break;
                    }
                    default: throw new NotImplementedException($"Not implemented message type {m.GetType().Name}");
                }     
            }
        }
    }

    Dictionary<string, ActiveRoom> _activeRooms = [];
    public async Task HandleConnection(string roomId, WebSocket socket, string userName)
    {
        ActiveRoom? room;
        lock (_activeRooms)
        {
            if (!_activeRooms.TryGetValue(roomId, out room))
            {
                // TODO: Validate roomId
                room = new ActiveRoom(this, roomId);
                _activeRooms.Add(roomId, room);
            }
        }
        await room.Post(new Connected(socket));
        
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var messageStream = WebSocketStream.CreateReadableMessageStream(socket);
                using var reader = new StreamReader(messageStream, Encoding.UTF8);

                string message = await reader.ReadToEndAsync();
       
                _logger.LogInformation("recieved message: '{0}'", message);
                if (message.StartsWith("msg "))
                {
                    string text = message["msg ".Length..];
                    await room.Post(new ChatMessage(userName, text));
                } 
                else if (message.StartsWith("event ")) {
                    string content = message["event ".Length..];

                    await room.Post(new CanvasEvent(content));
                } 
                else if (message.StartsWith("get_events"))
                {
                    int[] range = [.. message["get_events ".Length..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
                    int start = range[0];
                    int? end = range.Length > 1 ? range[1] : null;

                    await room.Post(new GetEvents(socket, start, end));
                }
            }
        } 
        finally
        {
            await room.Post(new Disconnected(socket));
        }
    }

}