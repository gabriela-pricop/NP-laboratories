using System.Net;
using System.Net.Sockets;

namespace UDPLanChatLab3;

public sealed class ChatPeer : IDisposable
{
    private const int ReceiveBuffer = 65536;

    private readonly Options options;
    private readonly DatagramLog log;
    private readonly Socket groupSocket;
    private readonly Socket directSocket;
    private readonly IPEndPoint groupEndPoint;
    private volatile bool closed;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Name => options.Name;

    public int DirectPort => ((IPEndPoint)directSocket.LocalEndPoint!).Port;

    public event Action<Message, IPEndPoint>? MessageReceived;

    public ChatPeer(Options options, DatagramLog log)
    {
        this.options = options;
        this.log = log;
        groupEndPoint = new IPEndPoint(options.Group, options.Port);

        groupSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        directSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            groupSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            groupSocket.Bind(new IPEndPoint(IPAddress.Any, options.Port));
            groupSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(options.Group, options.Interface));

            IgnoreIcmpResets(directSocket);
            directSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            directSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                options.Interface.GetAddressBytes());
            directSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            directSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        }
        catch
        {
            groupSocket.Dispose();
            directSocket.Dispose();
            throw;
        }
    }

    public void Start()
    {
        StartReceiving(groupSocket, "group receiver");
        StartReceiving(directSocket, "direct receiver");
    }

    public bool SendToGroup(Message message) => SendTo(message, groupEndPoint);

    public bool SendTo(Message message, IPEndPoint to)
    {
        byte[] datagram = Wire.Encode(message);
        log.Sent(to, message.Kind, datagram.Length);
        try
        {
            directSocket.SendTo(datagram, to);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            log.Error($"send {message.Kind} to {to} failed: {ex.Message}");
            return false;
        }
    }

    private void StartReceiving(Socket socket, string name)
    {
        var thread = new Thread(() => ReceiveLoop(socket)) { IsBackground = true, Name = name };
        thread.Start();
    }

    private void ReceiveLoop(Socket socket)
    {
        byte[] buffer = new byte[ReceiveBuffer];

        while (!closed)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int size;
            try
            {
                size = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException ex) when (!closed && ex.SocketErrorCode
                is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                break;
            }

            var from = (IPEndPoint)remote;
            if (!Wire.TryDecode(buffer.AsSpan(0, size), out Message? message, out string reason))
            {
                log.Dropped(from, "?", size, reason);
                continue;
            }

            log.Received(from, message!.Kind, size);
            try
            {
                MessageReceived?.Invoke(message, from);
            }
            catch (Exception ex)
            {
                log.Error($"handling {message.Kind} from {from}: {ex.Message}");
            }
        }
    }

    private static void IgnoreIcmpResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int SioUdpConnReset = unchecked((int)0x9800000C);
        socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    public void Dispose()
    {
        if (closed)
            return;
        closed = true;

        try
        {
            groupSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                new MulticastOption(options.Group, options.Interface));
        }
        catch (SocketException)
        {
        }

        groupSocket.Close();
        directSocket.Close();
    }
}
