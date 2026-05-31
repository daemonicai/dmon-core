using System.Net.WebSockets;
using System.Text;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// Wraps a <see cref="WebSocket"/> as an <see cref="IGatewayConnection"/>.
/// Each call to <see cref="SendAsync"/> sends exactly one UTF-8 text frame.
///
/// This type is the single funnel for every byte written to the socket: both the
/// session pump's drained event frames and the endpoint's control frames (<c>attached</c>,
/// <c>pong</c>) go through here. Concurrent <see cref="WebSocket.SendAsync"/> on one socket
/// is illegal, so a <see cref="SemaphoreSlim"/> serializes all sends — a client ping arriving
/// mid-drain (or the <c>attached</c> reply racing the first buffered event) can no longer
/// produce two outstanding sends on the same socket.
/// </summary>
internal sealed class WebSocketGatewayConnection : IGatewayConnection, IDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketGatewayConnection(WebSocket socket)
    {
        _socket = socket;
    }

    public async ValueTask SendAsync(string frame, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(frame);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                bytes.AsMemory(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose() => _sendLock.Dispose();
}
