using System.Net;

namespace UDPLanChatLab3;

public sealed record PeerInfo(string Name, string Id, IPEndPoint EndPoint, DateTime LastSeen);

public enum TouchResult
{
    New,
    Refreshed,
    Conflict,
}

public sealed class PeerTable
{
    private readonly object gate = new();
    private readonly Dictionary<string, PeerInfo> peers = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get { lock (gate) return peers.Count; }
    }

    public TouchResult Touch(string name, string id, IPEndPoint endPoint)
    {
        lock (gate)
        {
            if (peers.TryGetValue(name, out PeerInfo? known))
            {
                if (known.Id != id)
                    return TouchResult.Conflict;

                peers[name] = known with { EndPoint = endPoint, LastSeen = DateTime.UtcNow };
                return TouchResult.Refreshed;
            }

            peers[name] = new PeerInfo(name, id, endPoint, DateTime.UtcNow);
            return TouchResult.New;
        }
    }

    public void Refresh(string name, string id, IPEndPoint endPoint)
    {
        lock (gate)
        {
            if (peers.TryGetValue(name, out PeerInfo? known) && known.Id == id)
                peers[name] = known with { EndPoint = endPoint, LastSeen = DateTime.UtcNow };
        }
    }

    public bool Contains(string name)
    {
        lock (gate) return peers.ContainsKey(name);
    }

    public bool TryGet(string name, out PeerInfo peer)
    {
        lock (gate) return peers.TryGetValue(name, out peer!);
    }

    public bool Remove(string name, string id)
    {
        lock (gate)
        {
            return peers.TryGetValue(name, out PeerInfo? known) && known.Id == id && peers.Remove(name);
        }
    }

    public List<PeerInfo> RemoveSilentSince(DateTime cutoffUtc)
    {
        lock (gate)
        {
            List<PeerInfo> gone = peers.Values.Where(p => p.LastSeen < cutoffUtc).ToList();
            foreach (PeerInfo peer in gone)
                peers.Remove(peer.Name);
            return gone;
        }
    }

    public List<PeerInfo> Snapshot()
    {
        lock (gate) return peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
