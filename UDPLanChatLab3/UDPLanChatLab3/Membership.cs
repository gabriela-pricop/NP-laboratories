using System.Net;

namespace UDPLanChatLab3;

public sealed class Membership : IDisposable
{
    public static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(1);

    private readonly ChatPeer peer;
    private readonly PeerTable table;
    private readonly ManualResetEventSlim refused = new();
    private volatile bool joined;
    private volatile bool left;
    private string refusal = "";
    private Timer? timer;
    private DateTime nextBeat;

    public Membership(ChatPeer peer, PeerTable table)
    {
        this.peer = peer;
        this.table = table;
    }

    public bool Join(out string reason)
    {
        peer.SendToGroup(new Message(MessageType.Join, peer.Id, peer.Name));

        if (refused.Wait(JoinWait))
        {
            peer.SendToGroup(new Message(MessageType.Leave, peer.Id, peer.Name));
            reason = refusal;
            return false;
        }

        joined = true;
        nextBeat = DateTime.UtcNow + HeartbeatPeriod;
        timer = new Timer(_ => Tick(), null, TickPeriod, TickPeriod);
        reason = "";
        return true;
    }

    public void Leave()
    {
        if (left)
            return;
        left = true;

        timer?.Dispose();
        if (joined)
            peer.SendToGroup(new Message(MessageType.Leave, peer.Id, peer.Name));
    }

    public void Handle(Message message, IPEndPoint from)
    {
        if (message.Id == peer.Id)
            return;

        switch (message.Type)
        {
            case MessageType.Join:
                OnJoin(message, from);
                break;

            case MessageType.Here:
                table.Touch(message.Name, message.Id, from);
                break;

            case MessageType.Taken:
                if (!joined)
                {
                    refusal = $"the name {peer.Name} is already in use ({message.Name} says so)";
                    refused.Set();
                }
                break;

            case MessageType.Beat:
                if (table.Touch(message.Name, message.Id, from) == TouchResult.New && joined)
                    Output.Notice($"* {message.Name} joined the chat");
                break;

            case MessageType.Leave:
                if (table.Remove(message.Name, message.Id))
                    Output.Notice($"* {message.Name} left the chat");
                break;

            default:
                table.Refresh(message.Name, message.Id, from);
                break;
        }
    }

    private void OnJoin(Message message, IPEndPoint from)
    {
        if (Wire.NamesEqual(message.Name, peer.Name))
        {
            peer.SendTo(new Message(MessageType.Taken, peer.Id, peer.Name, Text: "name in use"), from);
            return;
        }

        TouchResult result = table.Touch(message.Name, message.Id, from);
        if (result == TouchResult.Conflict)
        {
            peer.SendTo(new Message(MessageType.Taken, peer.Id, peer.Name, Text: "name in use"), from);
            return;
        }

        peer.SendTo(new Message(MessageType.Here, peer.Id, peer.Name), from);

        if (result == TouchResult.New && joined)
            Output.Notice($"* {message.Name} joined the chat");
    }

    private void Tick()
    {
        if (left)
            return;

        DateTime now = DateTime.UtcNow;
        if (now >= nextBeat)
        {
            nextBeat = now + HeartbeatPeriod;
            peer.SendToGroup(new Message(MessageType.Beat, peer.Id, peer.Name));
        }

        foreach (PeerInfo gone in table.RemoveSilentSince(now - Silence))
            Output.Notice($"* {gone.Name} left the chat (not heard from for {Silence.TotalSeconds:0} s)");
    }

    public void Dispose()
    {
        timer?.Dispose();
        refused.Dispose();
    }
}
