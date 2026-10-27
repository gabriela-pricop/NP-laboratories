using System.Diagnostics;

namespace lab1.task1;

public class Task1A : IExample
{
    public const string WorkerFlag = "--task1a-worker";

    private const string FileName = "numbers.txt";
    private static readonly int[] ThreadCounts = { 1, 2, 4, 8, 16, 32, 64 };

    private readonly record struct Counts(long Negative, long Positive, long Zero);

    // Parent: launches one fresh process per thread count so peak memory is isolated
    // (it creates 7 copies and shows their output)
    public void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);

        foreach (int threads in ThreadCounts)
        {
            var startInfo = new ProcessStartInfo(
                Environment.ProcessPath!,
                $"{WorkerFlag} {threads} \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo)!;
            Console.Write(process.StandardOutput.ReadToEnd());
            process.WaitForExit();
        }
    }

    // Child: does one measured run with the given number of threads
    // (just one copy: creates the threads, times them, adds up the results, prints one line)
    public static void RunWorker(int threadCount, string path)
    {
        long size = new FileInfo(path).Length;
        long chunk = size / threadCount; // splitting numbers.txt in bytes

        var results = new Counts[threadCount];
        var threads = new Thread[threadCount];

        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < threadCount; i++)
        {
            int id = i;
            // each thread gets the half-open range [start, end)
            long start = id * chunk; 
            long end = id == threadCount - 1 ? size : (id + 1) * chunk; // setting `size` explicitly, in case of remainders
            // rule: "A line belongs to the thread whose range contains the line’s first byte."

            threads[id] = new Thread(() => results[id] = CountRange(path, start, end));
            threads[id].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        stopwatch.Stop();

        long negative = results.Sum(r => r.Negative);
        long positive = results.Sum(r => r.Positive);
        long zero = results.Sum(r => r.Zero);
        double peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0;

        Console.WriteLine(
            $"{threadCount,2} threads | neg={negative} pos={positive} zero={zero} | " +
            $"{stopwatch.ElapsedMilliseconds} ms | peak {peakMb:F1} MB");
    }

    // A line belongs to the thread whose range contains the line's first byte
    // (what each thread runs: reads its own piece and counts)
    private static Counts CountRange(string path, long start, long end)
    {
        long negative = 0, positive = 0, zero = 0;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.Read, 1 << 16);
        var buffer = new byte[1 << 16];
        int length = 0, index = 0;

        
        // A helper that reads the file in big chunks (64 KB) so it’s fast, but hands out one character at a time
        int NextByte()
        {
            if (index == length)
            {
                length = fs.Read(buffer, 0, buffer.Length);
                index = 0;
                if (length == 0) return -1;
            }
            return buffer[index++];
        }

        long position; // absolute file offset of the next byte to be read

        if (start > 0)
        {
            // Skip the (possibly partial) line owned by the previous thread
            fs.Seek(start - 1, SeekOrigin.Begin); // we go one byte back
            // read until we pass "\n"
            position = start - 1;

            int skipped;
            do
            {
                skipped = NextByte();
                position++;
            } while (skipped != -1 && skipped != '\n');
        }
        else
        {
            position = 0;
        }

        // Process every line that STARTS before 'end'
        while (position < end)
        {
            long value = 0;
            bool isNegative = false, hasDigits = false;
            int b;

            while ((b = NextByte()) != -1)
            {
                position++;

                if (b == '\n') break;

                if (b == '-')
                {
                    isNegative = true;
                }
                else if (b >= '0' && b <= '9')
                {
                    hasDigits = true;
                    value = value * 10 + (b - '0');
                }
                // '\r', spaces and '+' are ignored
            }

            if (hasDigits)
            {
                if (value == 0) zero++;
                else if (isNegative) negative++;
                else positive++;
            }

            if (b == -1) break; // end of file
        }

        return new Counts(negative, positive, zero);
    }
    
}