using System.Net;
using System.Net.Sockets;
using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public sealed class ServerHost
{
    private readonly IPEndPoint _endpoint;
    private readonly Shop _shop;

    public ServerHost(IPEndPoint endpoint, Shop shop)
    {
        _endpoint = endpoint;
        _shop = shop;
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            listener.Bind(_endpoint);
            listener.Listen(backlog: 128);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Log.Server($"port {_endpoint.Port} is already in use, stop the other program or choose another port");
            return;
        }

        Log.Server($"listening on {_endpoint}");

        var sessions = new List<Task>();

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket connection;

                try
                {
                    connection = await listener.AcceptAsync(stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Log.Error("listener", $"accept failed: {ex.SocketErrorCode}");
                    continue;
                }

                Configure(connection);

                var session = new ClientSession(new LineSocket(connection), _shop);
                sessions.Add(session.RunAsync(stopping));
                sessions.RemoveAll(task => task.IsCompleted);
            }
        }
        finally
        {
            Log.Server("closing");
            _shop.Close();

            try
            {
                await Task.WhenAll(sessions).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Log.Server("some sessions ended with an error");
            }
        }
    }

    private static void Configure(Socket connection)
    {
        connection.NoDelay = true;

        try
        {
            connection.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            connection.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            connection.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            connection.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException)
        {
            Log.Server("tcp keep-alive is not available on this platform");
        }
        catch (PlatformNotSupportedException)
        {
            Log.Server("tcp keep-alive is not available on this platform");
        }
    }
}
