using System.Diagnostics;
using lab1;

namespace Lab1.Task2;

public class Task2 : IExample
{
    private const int FileCount = 8;
    private const long ExpectedTotal = 50_000_000;
    private static readonly int[] ThreadCounts = { 1, 2, 4, 6 };

    public void Run()
    {
        string directory = "E:\\labGabi";
        string[] files = BuildFileList(directory);

        Console.WriteLine($"Reading files from: {directory}");
        Console.WriteLine();

        var results = new List<(int Threads, double Seconds, double PeakMb, Counts Counts)>();
        Counts? reference = null;

        foreach (int threads in ThreadCounts)
        {
            Console.WriteLine($"--- {threads} thread(s) ---");
            Console.WriteLine("Eject the USB drive, unplug it, plug it back in (so no data comes from the file cache),");
            Console.Write("then press Enter to start the run... ");
            Console.ReadLine();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var memory = new PeakMemorySampler();
            memory.Start();
            var stopwatch = Stopwatch.StartNew();

            Counts total = CountWithThreads(files, threads);

            stopwatch.Stop();
            memory.Stop();

            double seconds = stopwatch.Elapsed.TotalSeconds;
            double peakMb = memory.PeakBytes / (1024.0 * 1024.0);
            double deltaMb = (memory.PeakBytes - memory.BaselineBytes) / (1024.0 * 1024.0);

            Console.WriteLine($"Negative: {total.Negative:N0}");
            Console.WriteLine($"Positive: {total.Positive:N0}");
            Console.WriteLine($"Zero:     {total.Zero:N0}");
            Console.WriteLine($"Sum:      {total.Total:N0} ({(total.Total == ExpectedTotal ? "OK" : "WRONG, expected " + ExpectedTotal.ToString("N0"))})");
            Console.WriteLine($"Time:     {seconds:F3} s");
            Console.WriteLine($"Peak memory (working set): {peakMb:F1} MB (+{deltaMb:F1} MB over the start of the run)");
            Console.WriteLine();

            reference ??= total;
            if (total != reference.Value)
                Console.WriteLine("WARNING: the totals differ from the first run!");

            results.Add((threads, seconds, peakMb, total));
        }

        PrintSummary(results);
    }
    
    private static Counts CountWithThreads(string[] files, int threadCount)
    {
        var partial = new Counts[threadCount];
        var threads = new Thread[threadCount];

        int baseCount = files.Length / threadCount;
        int remainder = files.Length % threadCount;
        int start = 0;

        for (int t = 0; t < threadCount; t++)
        {
            int take = baseCount + (t < remainder ? 1 : 0);
            string[] assigned = files.Skip(start).Take(take).ToArray();
            start += take;

            int index = t; 
            threads[t] = new Thread(() =>
            {
                Counts local = default;
                foreach (string path in assigned)
                {
                    byte[] buffer = File.ReadAllBytes(path); 
                    local += ParseAndCount(buffer);
                }
                partial[index] = local;
            })
            { Name = $"worker-{t + 1}" };
        }

        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        Counts sum = default;
        foreach (Counts c in partial) sum += c;
        return sum;
    }

    private static Counts ParseAndCount(byte[] buffer)
    {
        long negative = 0, positive = 0, zero = 0;
        int i = 0;
        int n = buffer.Length;

        while (i < n)
        {
            byte c = buffer[i];

            if (c == (byte)'-' || (c >= (byte)'0' && c <= (byte)'9'))
            {
                bool isNegative = false;
                if (c == (byte)'-')
                {
                    isNegative = true;
                    i++;
                }

                long value = 0;
                bool hasDigits = false;
                while (i < n && buffer[i] >= (byte)'0' && buffer[i] <= (byte)'9')
                {
                    value = value * 10 + (buffer[i] - (byte)'0');
                    hasDigits = true;
                    i++;
                }

                if (hasDigits)
                {
                    if (value == 0) zero++;
                    else if (isNegative) negative++;
                    else positive++;
                }
            }
            else
            {
                i++;
            }
        }

        return new Counts(negative, positive, zero);
    }


    private static string[] BuildFileList(string directory)
    {
        var files = new string[FileCount];
        for (int i = 0; i < FileCount; i++)
        {
            files[i] = Path.Combine(directory, $"numbers_part_{i + 1}.txt");
            if (!File.Exists(files[i]))
                throw new FileNotFoundException($"Missing file: {files[i]}");
        }
        return files;
    }


    private static void PrintSummary(List<(int Threads, double Seconds, double PeakMb, Counts Counts)> results)
    {
        double baseline = results[0].Seconds;

        Console.WriteLine("=== Summary (Task 2) ===");
        Console.WriteLine($"{"Threads",8} {"Time [s]",10} {"Speed-up",10} {"Peak mem [MB]",15}");
        foreach (var r in results)
            Console.WriteLine($"{r.Threads,8} {r.Seconds,10:F3} {baseline / r.Seconds,10:F2} {r.PeakMb,15:F1}");

        bool allEqual = results.All(r => r.Counts == results[0].Counts);
        bool allSumOk = results.All(r => r.Counts.Total == ExpectedTotal);
        Console.WriteLine();
        Console.WriteLine($"Same totals in all runs: {(allEqual ? "yes" : "NO")}");
        Console.WriteLine($"All sums equal {ExpectedTotal:N0}: {(allSumOk ? "yes" : "NO")}");
    }
}