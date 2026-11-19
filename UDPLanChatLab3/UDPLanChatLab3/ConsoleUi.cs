using System.Net;

namespace UDPLanChatLab3;

public static class Output
{
    private static readonly object Gate = new();

    public static void Line(string text)
    {
        lock (Gate) Console.WriteLine(text);
    }

    public static void Notice(string text) => Line(text);
}

public sealed class ConsoleUi
{
    private const string Help =
        """
          text               sends text to everybody (the general channel)
          /msg name text     sends text to name only (a private message)
          /users             shows who is online
          /quit              leaves the chat
        """;

    private readonly ChatPeer peer;
    private readonly PeerTable table;
    private readonly SecureBox box;
    private readonly DatagramLog log;

    public ConsoleUi(ChatPeer peer, PeerTable table, SecureBox box, DatagramLog log)
    {
        this.peer = peer;
        this.table = table;
        this.box = box;
        this.log = log;
    }

    public void Run()
    {
        while (Console.ReadLine() is { } line)
        {
            line = line.TrimEnd();
            if (line.Length == 0)
                continue;

            if (!line.StartsWith('/'))
            {
                SendGeneral(line);
                continue;
            }

            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "/msg" when parts.Length == 3:
                    SendPrivate(parts[1], parts[2]);
                    break;
                case "/msg":
                    Output.Line("! usage: /msg name text");
                    break;
                case "/users":
                    ShowUsers();
                    break;
                case "/quit":
                    return;
                case "/help":
                    Output.Line(Help);
                    break;
                default:
                    Output.Line($"! unknown command {parts[0]}; /help lists the commands");
                    break;
            }
        }
    }

    public void Handle(Message message, IPEndPoint from)
    {
        switch (message.Type)
        {
            case MessageType.Chat:
                Output.Line($"[general] {message.Name}: {message.Text}");
                break;

            case MessageType.Private when message.Id != peer.Id:
                ShowPrivate(message, from);
                break;
        }
    }

    private void SendGeneral(string text)
    {
        if (text.Length > Wire.MaxText)
        {
            Output.Line($"! a message has at most {Wire.MaxText} characters (this one has {text.Length})");
            return;
        }

        peer.SendToGroup(new Message(MessageType.Chat, peer.Id, peer.Name, Text: text));
    }

    private void SendPrivate(string name, string text)
    {
        if (text.Length > Wire.MaxText)
        {
            Output.Line($"! a message has at most {Wire.MaxText} characters (this one has {text.Length})");
            return;
        }

        if (Wire.NamesEqual(name, peer.Name))
        {
            Output.Line("! that is you");
            return;
        }

        if (!table.TryGet(name, out PeerInfo receiver))
        {
            Output.Line($"! {name} is not online");
            return;
        }

        (string nonce, string data) = box.Seal(text, peer.Name, receiver.Name);
        var message = new Message(MessageType.Private, peer.Id, peer.Name, To: receiver.Name, Nonce: nonce, Data: data);

        if (peer.SendTo(message, receiver.EndPoint))
            Output.Line($"[private to {receiver.Name}] {peer.Name}: {text}");
        else
            Output.Line($"! could not send to {receiver.Name}");
    }

    private void ShowPrivate(Message message, IPEndPoint from)
    {
        if (!Wire.NamesEqual(message.To!, peer.Name))
        {
            log.Dropped(from, message.Kind, 0, $"private message for {message.To}, not for us");
            return;
        }

        if (!box.TryOpen(message.Nonce!, message.Data!, message.Name, message.To!, out string text))
        {
            log.Dropped(from, message.Kind, 0, "cannot decrypt (wrong passphrase or changed on the way)");
            Output.Line($"! a private message from {message.Name} could not be decrypted");
            return;
        }

        if (text.Length is 0 or > Wire.MaxText)
        {
            log.Dropped(from, message.Kind, 0, "private text empty or too long");
            return;
        }

        Output.Line($"[private] {message.Name}: {text}");
    }

    private void ShowUsers()
    {
        List<PeerInfo> others = table.Snapshot();
        Output.Line($"* online ({others.Count + 1}):");
        Output.Line($"    {peer.Name} (you)");
        foreach (PeerInfo p in others)
        {
            double ago = (DateTime.UtcNow - p.LastSeen).TotalSeconds;
            Output.Line($"    {p.Name,-20} {p.EndPoint,-21} heard {ago:0} s ago");
        }
    }
}
