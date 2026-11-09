using System.Net;
using System.Net.Sockets;
using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Client;

public static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 5050;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
        {
            Usage();
            return 0;
        }

        string role = args.Length > 0 ? args[0].ToLowerInvariant() : "client";

        if (role is not ("client" or "provider"))
        {
            Console.Error.WriteLine($"unknown role '{args[0]}'");
            Usage();
            return 1;
        }

        string host = args.Length > 1 ? args[1] : DefaultHost;

        int port = DefaultPort;

        if (args.Length > 2 && !int.TryParse(args[2], out port))
        {
            Console.Error.WriteLine($"'{args[2]}' is not a port number");
            return 1;
        }

        IPEndPoint endpoint;

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            IPAddress? ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);

            if (ipv4 is null)
            {
                Console.Error.WriteLine($"no IPv4 address for '{host}'");
                return 1;
            }

            endpoint = new IPEndPoint(ipv4, port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot resolve '{host}': {ex.Message}");
            return 1;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await socket.ConnectAsync(endpoint).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine(ex.SocketErrorCode == SocketError.ConnectionRefused
                ? $"nobody is listening on {endpoint}, is the server started?"
                : $"cannot connect to {endpoint}: {ex.SocketErrorCode}");

            socket.Dispose();
            return 1;
        }

        using var connection = new LineSocket(socket);
        using var stopping = new CancellationTokenSource();

        Console.WriteLine($"connected to {endpoint} as {role}");
        Console.WriteLine("type 'help' for the commands, 'quit' to leave");

        await connection
            .SendLineAsync($"{Protocol.Hello} {role.ToUpperInvariant()}", stopping.Token)
            .ConfigureAwait(false);

        Task receiving = ReceiveLoopAsync(connection, stopping);
        Task typing = Task.Run(() => InputLoopAsync(connection, stopping));

        await Task.WhenAny(receiving, typing).ConfigureAwait(false);
        stopping.Cancel();

        return 0;
    }

    private static async Task ReceiveLoopAsync(LineSocket connection, CancellationTokenSource stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                LineResult result = await connection
                    .ReceiveLineAsync(stopping.Token)
                    .ConfigureAwait(false);

                if (result.Kind == LineKind.Closed)
                {
                    Console.WriteLine();
                    Console.WriteLine("the server is gone, closing");
                    return;
                }

                if (result.Kind == LineKind.TooLong)
                {
                    Console.WriteLine("<< a message from the server was too long and was dropped");
                    continue;
                }

                Console.WriteLine($"<< {Describe(result.Text)}");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private static async Task InputLoopAsync(LineSocket connection, CancellationTokenSource stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                return;
            }

            if (line is null)
            {
                await connection.SendLineAsync(Protocol.Quit, CancellationToken.None).ConfigureAwait(false);
                stopping.Cancel();
                return;
            }

            line = line.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (string.Equals(line, "help", StringComparison.OrdinalIgnoreCase))
            {
                Help();
                continue;
            }

            if (line.Length > Protocol.MaxLineLength)
            {
                Console.WriteLine($"too long, the server accepts at most {Protocol.MaxLineLength} characters");
                continue;
            }

            string[] parts = line.Split(' ', 2);

            string message = parts.Length == 1
                ? parts[0].ToUpperInvariant()
                : $"{parts[0].ToUpperInvariant()} {parts[1]}";

            bool sent = await connection.SendLineAsync(message, stopping.Token).ConfigureAwait(false);

            if (!sent)
            {
                Console.WriteLine("could not send, the connection is gone");
                stopping.Cancel();
                return;
            }

            if (string.Equals(parts[0], Protocol.Quit, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(200).ConfigureAwait(false);
                stopping.Cancel();
                return;
            }
        }
    }

    private static string Describe(string line)
    {
        string[] tokens = line.Split(' ');

        return tokens[0] switch
        {
            Protocol.Accepted when tokens.Length >= 3 =>
                $"order #{tokens[1]} accepted, ready in {tokens[2]}s",
            Protocol.Refused when tokens.Length >= 3 =>
                $"refused, only {tokens[2]} {tokens[1]} left",
            Protocol.Ready when tokens.Length >= 2 =>
                $"*** order #{tokens[1]} is ready ***",
            Protocol.Alert when tokens.Length >= 3 =>
                $"*** low stock: {tokens[1]}, {tokens[2]} left ***",
            _ => line,
        };
    }

    private static void Usage()
    {
        Console.WriteLine("usage: CoffeeShopLab2.Client [client|provider] [host] [port]");
        Console.WriteLine($"  defaults: client {DefaultHost} {DefaultPort}");
    }

    private static void Help()
    {
        Console.WriteLine("  menu                     the products and the stock");
        Console.WriteLine("  order coffee 2 tea 1     place an order");
        Console.WriteLine("  deliver coffee 20        provider only, bring products");
        Console.WriteLine("  ping                     check the connection");
        Console.WriteLine("  quit                     leave");
    }
}
