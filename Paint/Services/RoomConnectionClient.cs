using System.Net.WebSockets;
using System.Text.Json;

namespace Paint.Services;

public sealed class RoomConnectionClient : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public RoomConnectionClient(string connectionId, string userName, WebSocket socket)
    {
        ConnectionId = connectionId;
        UserName = userName;
        Socket = socket;
    }

    public string ConnectionId { get; }

    public string UserName { get; }

    public WebSocket Socket { get; }

    public async Task<bool> SendAsync(
        object message,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        if (Socket.State != WebSocketState.Open)
        {
            return false;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, serializerOptions);

        var lockTaken = false;
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            lockTaken = true;

            if (Socket.State != WebSocketState.Open)
            {
                return false;
            }

            await Socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (lockTaken)
            {
                _sendLock.Release();
            }
        }
    }

    public void Dispose()
    {
        _sendLock.Dispose();
    }
}
