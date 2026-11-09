using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoffeeShopLab2.Shared;

public enum LineKind
{
    Line,
    TooLong,
    Closed,
}

public readonly record struct LineResult(LineKind Kind, string Text)
{
    public static LineResult Closed { get; } = new(LineKind.Closed, string.Empty);

    public static LineResult TooLong { get; } = new(LineKind.TooLong, string.Empty);
}

public sealed class LineSocket : IDisposable
{
    private readonly Socket _socket;
    private readonly LineFramer _framer = new(Protocol.MaxLineLength);
    private readonly byte[] _receiveBuffer = new byte[4096];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public LineSocket(Socket socket)
    {
        _socket = socket;

        EndPoint? remote = null;

        try
        {
            remote = socket.RemoteEndPoint;
        }
        catch (ObjectDisposedException)
        {
            remote = null;
        }
        catch (SocketException)
        {
            remote = null;
        }

        Endpoint = remote?.ToString() ?? "unknown";
    }

    public string Endpoint { get; }

    public async Task<LineResult> ReceiveLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_framer.TryTake(out string line, out bool wasTooLong))
            {
                return wasTooLong ? LineResult.TooLong : new LineResult(LineKind.Line, line);
            }

            int count;

            try
            {
                count = await _socket
                    .ReceiveAsync(_receiveBuffer.AsMemory(), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return LineResult.Closed;
            }
            catch (ObjectDisposedException)
            {
                return LineResult.Closed;
            }

            if (count == 0)
            {
                return LineResult.Closed;
            }

            _framer.Feed(_receiveBuffer.AsSpan(0, count));
        }
    }

    public async Task<bool> SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            int sent = 0;

            while (sent < bytes.Length)
            {
                int written = await _socket
                    .SendAsync((ReadOnlyMemory<byte>)bytes.AsMemory(sent), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);

                if (written <= 0)
                {
                    return false;
                }

                sent += written;
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
                }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _socket.Close();
        _sendLock.Dispose();
    }
}
