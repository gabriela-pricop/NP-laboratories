using System.Diagnostics;

namespace Lab1.Task2;

public sealed class PeakMemorySampler
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly int _intervalMs;
    private Thread? _thread;
    private volatile bool _running;
    private long _peakBytes;

    public PeakMemorySampler(int intervalMs = 2)
    {
        _intervalMs = intervalMs;
    }

    public long BaselineBytes { get; private set; }
    public long PeakBytes => Interlocked.Read(ref _peakBytes);

    public void Start()
    {
        _process.Refresh();
        BaselineBytes = _process.WorkingSet64;
        Interlocked.Exchange(ref _peakBytes, BaselineBytes);
        _running = true;
        _thread = new Thread(SampleLoop) { IsBackground = true, Name = "memory-sampler" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        Sample(); 
    }

    private void SampleLoop()
    {
        while (_running)
        {
            Sample();
            Thread.Sleep(_intervalMs);
        }
    }

    private void Sample()
    {
        _process.Refresh();
        long current = _process.WorkingSet64;
        long seen;
        while (current > (seen = Interlocked.Read(ref _peakBytes)))
        {
            if (Interlocked.CompareExchange(ref _peakBytes, current, seen) == seen)
                break;
        }
    }
}