using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UDPLanChatLab3;

public sealed class Options
{
    public static readonly IPAddress DefaultGroup = IPAddress.Parse("239.255.42.99");

    public const string Usage =
        """
        Usage: UDPLanChatLab3 <name> <port> <passphrase> [--iface <local IPv4>] [--group <multicast IPv4>]

          name        1-20 letters, digits, '_', '-' or '.'; unique in the chat
          port        the port of the chat, the same for every peer (e.g. 5000)
          passphrase  shared by every peer; encrypts the private messages
          --iface     the local address of the network card to use (default: chosen automatically)
          --group     the multicast group (default: 239.255.42.99)

        Example: dotnet run --project UDPLanChatLab3 -- Ana 5000 "secret words"
        """;

    public required string Name { get; init; }
    public required int Port { get; init; }
    public required string Passphrase { get; init; }
    public required IPAddress Interface { get; init; }
    public required IPAddress Group { get; init; }

    public static bool TryParse(string[] args, [NotNullWhen(true)] out Options? options, out string error)
    {
        options = null;
        error = "";
        var positional = new List<string>();
        IPAddress? iface = null;
        IPAddress group = DefaultGroup;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--iface" or "--group")
            {
                if (i + 1 >= args.Length || !IPAddress.TryParse(args[i + 1], out IPAddress? address)
                    || address.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = $"{arg} needs an IPv4 address.";
                    return false;
                }

                if (arg == "--iface") iface = address;
                else group = address;
                i++;
            }
            else
            {
                positional.Add(arg);
            }
        }

        if (positional.Count != 3)
        {
            error = "Expected exactly three parameters: name, port and passphrase.";
            return false;
        }

        if (!Wire.IsValidName(positional[0]))
        {
            error = "The name must be 1-20 letters, digits, '_', '-' or '.'.";
            return false;
        }

        if (!int.TryParse(positional[1], out int port) || port is < 1 or > 65535)
        {
            error = "The port must be a number between 1 and 65535.";
            return false;
        }

        if (positional[2].Length == 0)
        {
            error = "The passphrase cannot be empty.";
            return false;
        }

        byte first = group.GetAddressBytes()[0];
        if (first is < 224 or > 239)
        {
            error = "The group must be a multicast address (224.0.0.0 - 239.255.255.255).";
            return false;
        }

        options = new Options
        {
            Name = positional[0],
            Port = port,
            Passphrase = positional[2],
            Interface = iface ?? PickInterface(),
            Group = group,
        };
        return true;
    }

    private static IPAddress PickInterface()
    {
        IPAddress? withoutGateway = null;

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast)
                continue;

            IPInterfaceProperties properties = nic.GetIPProperties();
            IPAddress? address = properties.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
                continue;

            bool hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (hasGateway)
                return address;

            withoutGateway ??= address;
        }

        return withoutGateway ?? IPAddress.Loopback;
    }
}
