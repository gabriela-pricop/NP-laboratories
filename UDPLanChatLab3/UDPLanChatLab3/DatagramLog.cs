using System.Net;

namespace UDPLanChatLab3;

public sealed class DatagramLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;

    public string Path { get; }

    public DatagramLog(string fileName)
    {
        Path = System.IO.Path.GetFullPath(fileName);
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer = new StreamWriter(stream) { AutoFlush = true };
        Write("----", "-", "start", 0, "");
    }

    public void Sent(EndPoint to, string kind, int size) =>
        Write("SEND", to.ToString() ?? "?", kind, size, "");

    public void Received(EndPoint from, string kind, int size) =>
        Write("RECV", from.ToString() ?? "?", kind, size, "");

    public void Dropped(EndPoint from, string kind, int size, string reason) =>
        Write("DROP", from.ToString() ?? "?", kind, size, reason);

    public void Error(string what) =>
        Write("ERR ", "-", "-", 0, what);

    private void Write(string direction, string endpoint, string kind, int size, string note)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {direction}  {endpoint,-21}  {kind,-6}  {size,5} B";
        if (note.Length > 0)
            line += $"  {note}";

        lock (gate)
        {
            try
            {
                writer.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer.Dispose();
        }
    }
}
