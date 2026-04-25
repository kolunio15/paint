using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Paint;

class RoomConnections(ILogger<RoomConnections> logger)
{
    ILogger<RoomConnections> _logger = logger;

    class ActiveRoom
    {
        public record Message;
        public record Connected(WebSocket Socket)    : Message;
        public record Disconnected(WebSocket Socket) : Message;
        public record ChatMessage(string UserName, string Text) : Message;

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
        await room.Post(new ActiveRoom.Connected(socket));
        
        try
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogInformation("recieved message: '{0}'", message);
                if (message.StartsWith("msg "))
                {
                    string text = message["msg ".Length..];

                    if (!result.EndOfMessage) {
                        _logger.LogInformation("Recieved too long chat message message, aborting connection.");
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Chat message is too long", CancellationToken.None); 
                        return;
                    }

                    await room.Post(new ActiveRoom.ChatMessage(userName, text));
                }
            }
        } 
        finally
        {
            await room.Post(new ActiveRoom.Disconnected(socket));
        }
    }

}