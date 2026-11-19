namespace UDPLanChatLab3;

public static class Program
{
    public static int Main(string[] args)
    {
        if (!Options.TryParse(args, out Options? options, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }

        using var log = new DatagramLog($"chat-{options.Name}.log");

        Output.Notice($"* deriving the key from the passphrase...");
        var box = new SecureBox(options.Passphrase);

        ChatPeer peer;
        try
        {
            peer = new ChatPeer(options, log);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot open the sockets: {ex.Message}");
            return 1;
        }

        using (peer)
        {
            var table = new PeerTable();
            using var membership = new Membership(peer, table);
            var ui = new ConsoleUi(peer, table, box, log);

            peer.MessageReceived += membership.Handle;
            peer.MessageReceived += ui.Handle;

            Output.Notice($"* {options.Name}: group {options.Group}:{options.Port} " +
                          $"on interface {options.Interface}, private messages on port {peer.DirectPort}");
            Output.Notice($"* log file: {log.Path}");

            peer.Start();

            if (!membership.Join(out string reason))
            {
                Output.Notice($"* cannot join: {reason}");
                return 2;
            }

            Output.Notice($"* you joined the chat as {options.Name}; " +
                          $"{table.Count} other(s) online. Type /help for the commands.");

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                membership.Leave();
                peer.Dispose();
                Environment.Exit(0);
            };

            ui.Run();
            membership.Leave();
        }

        return 0;
    }
}
