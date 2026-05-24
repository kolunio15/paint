using System.Collections.Concurrent;
using System.Text.Json;

namespace Paint.Services;

public class ActiveRoom
{
    private readonly ConcurrentDictionary<string, RoomConnectionClient> _connections = new();

    public ActiveRoom(string roomId)
    {
        RoomId = roomId;
    }

    public string RoomId { get; }

    public int UserCount => _connections.Count;

    public bool AddConnection(RoomConnectionClient client)
    {
        return _connections.TryAdd(client.ConnectionId, client);
    }

    public bool RemoveConnection(string connectionId, out RoomConnectionClient? client)
    {
        return _connections.TryRemove(connectionId, out client);
    }

    public async Task BroadcastAsync(
        object message,
        JsonSerializerOptions serializerOptions,
        string? exceptConnectionId = null,
        CancellationToken cancellationToken = default)
    {
        var clients = _connections.Values
            .Where(client => client.ConnectionId != exceptConnectionId)
            .ToList();

        var failedConnectionIds = new ConcurrentBag<string>();
        var sends = clients.Select(async client =>
        {
            var sent = await client.SendAsync(message, serializerOptions, cancellationToken);
            if (!sent)
            {
                failedConnectionIds.Add(client.ConnectionId);
            }
        });

        await Task.WhenAll(sends);

        foreach (var failedConnectionId in failedConnectionIds)
        {
            if (RemoveConnection(failedConnectionId, out var failedClient))
            {
                failedClient.Dispose();
            }
        }
    }
}
