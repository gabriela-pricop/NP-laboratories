using System.Collections.Concurrent;
using System.Diagnostics;

namespace lab1.task1;

public class Task1C : IExample
{
    public const string WorkerFlag = "--task1c-worker";

    private const string FileName = "numbers.txt";
    private const int BatchSize = 10_000;
    private const int QueueCapacity = 100;
    private static readonly int[] WorkerCounts = { 1, 2, 4, 8, 16, 32, 64 };

    // Parent: one fresh process per worker count, so peak memory is isolated
    public void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);

        foreach (int workers in WorkerCounts)
        {
            var startInfo = new ProcessStartInfo(
                Environment.ProcessPath!,
                $"{WorkerFlag} {workers} \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo)!;
            Console.Write(process.StandardOutput.ReadToEnd());
            process.WaitForExit();
        }
    }

    // Child: one measured run with the given number of workers
    public static void RunWorker(int workerCount, string path)
    {
        // Bounded, blocking, thread-safe queue:
        //   Add() blocks when 100 batches are waiting,
        //   GetConsumingEnumerable() blocks when empty and ends after CompleteAdding() + empty.
        using var queue = new BlockingCollection<List<string>>(QueueCapacity);

        var results = new (long Neg, long Pos, long Zero)[workerCount];
        var workers = new Thread[workerCount];

        var stopwatch = Stopwatch.StartNew();

        // Workers
        for (int i = 0; i < workerCount; i++)
        {
            int id = i;
            workers[id] = new Thread(() => results[id] = Consume(queue));
            workers[id].Start();
        }

        // Reader (one thread)
        var reader = new Thread(() => Produce(queue, path));
        reader.Start();

        reader.Join();
        foreach (var worker in workers)
            worker.Join();

        stopwatch.Stop();

        long neg = results.Sum(r => r.Neg);
        long pos = results.Sum(r => r.Pos);
        long zero = results.Sum(r => r.Zero);
        double peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0;

        Console.WriteLine(
            $"{workerCount,2} workers | neg={neg} pos={pos} zero={zero} | " +
            $"{stopwatch.ElapsedMilliseconds} ms | peak {peakMb:F1} MB");
    }

    // The reader: file -> batches of 10 000 lines -> queue
    private static void Produce(BlockingCollection<List<string>> queue, string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var batch = new List<string>(BatchSize);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                batch.Add(line);

                if (batch.Count == BatchSize)
                {
                    queue.Add(batch);               // blocks while the queue is full
                    batch = new List<string>(BatchSize);
                }
            }

            if (batch.Count > 0)
                queue.Add(batch);                   // last, partial batch
        }
        finally
        {
            queue.CompleteAdding();                 // always close, so workers never hang
        }
    }

    // A worker: take batch, count it, repeat until the queue is closed and empty
    private static (long Neg, long Pos, long Zero) Consume(BlockingCollection<List<string>> queue)
    {
        long neg = 0, pos = 0, zero = 0;

        foreach (var batch in queue.GetConsumingEnumerable())   // blocks while empty
        {
            foreach (string line in batch)
            {
                if (line.Length == 0) continue;

                int value = int.Parse(line);
                if (value < 0) neg++;
                else if (value > 0) pos++;
                else zero++;
            }
        }

        return (neg, pos, zero);
    }
}