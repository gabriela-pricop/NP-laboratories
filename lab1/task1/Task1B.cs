using System.Diagnostics;

namespace lab1.task1;

public class Task1B : IExample
{
    private const string FileName = "numbers.txt";

    // Shared counters used by versions 1 and 2
    private static long _negative, _positive, _zero;
    private static readonly object CounterLock = new();

    public void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);

        // Load everything into an array (not measured)
        int[] numbers = File.ReadLines(path)
                            .Where(line => line.Length > 0)
                            .Select(int.Parse)
                            .ToArray();

        int threadCount = Environment.ProcessorCount;

        // Correct answer, computed on one thread, only for comparison (not measured)
        long expNeg = 0, expPos = 0, expZero = 0;
        foreach (int v in numbers)
        {
            if (v < 0) expNeg++;
            else if (v > 0) expPos++;
            else expZero++;
        }

        Console.WriteLine($"Numbers: {numbers.Length}, threads: {threadCount}");
        Console.WriteLine($"Expected: neg={expNeg} pos={expPos} zero={expZero}");
        Console.WriteLine();

        // Version 1: shared counters, no synchronisation (run 3 times)
        for (int run = 1; run <= 3; run++)
        {
            ResetShared();
            long ms = RunThreads(numbers, threadCount, (from, to, _) => CountUnsynchronised(numbers, from, to));
            Print($"1. No sync, run {run}", _negative, _positive, _zero, ms, expNeg, expPos, expZero);
        }

        // Version 2: shared counters protected by a lock
        {
            ResetShared();
            long ms = RunThreads(numbers, threadCount, (from, to, _) => CountWithLock(numbers, from, to));
            Print("2. Lock", _negative, _positive, _zero, ms, expNeg, expPos, expZero);
        }

        // Version 3: one counter per thread, summed after all threads finish
        {
            var results = new (long Neg, long Pos, long Zero)[threadCount];
            long ms = RunThreads(numbers, threadCount,
                (from, to, id) => results[id] = CountLocal(numbers, from, to));

            long neg = results.Sum(r => r.Neg);
            long pos = results.Sum(r => r.Pos);
            long zero = results.Sum(r => r.Zero);
            Print("3. Per-thread counters", neg, pos, zero, ms, expNeg, expPos, expZero);
        }
    }

    private static void ResetShared()
    {
        _negative = 0;
        _positive = 0;
        _zero = 0;
    }

    // Splits the array into equal index ranges, runs 'work' on each in its own thread,
    // and returns the elapsed milliseconds (thread creation + work + join)
    private static long RunThreads(int[] numbers, int threadCount, Action<int, int, int> work)
    {
        int chunk = numbers.Length / threadCount;
        var threads = new Thread[threadCount];

        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < threadCount; i++)
        {
            int id = i;
            int from = id * chunk;
            int to = id == threadCount - 1 ? numbers.Length : from + chunk;

            threads[id] = new Thread(() => work(from, to, id));
            threads[id].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    // Version 1: data race on purpose, updates get lost
    private static void CountUnsynchronised(int[] numbers, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            int v = numbers[i];
            if (v < 0) _negative++;
            else if (v > 0) _positive++;
            else _zero++;
        }
    }

    // Version 2: correct, but every single increment takes the lock
    private static void CountWithLock(int[] numbers, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            int v = numbers[i];
            lock (CounterLock)
            {
                if (v < 0) _negative++;
                else if (v > 0) _positive++;
                else _zero++;
            }
        }
    }

    // Version 3: local variables only, nothing shared while counting
    private static (long Neg, long Pos, long Zero) CountLocal(int[] numbers, int from, int to)
    {
        long neg = 0, pos = 0, zero = 0;
        for (int i = from; i < to; i++)
        {
            int v = numbers[i];
            if (v < 0) neg++;
            else if (v > 0) pos++;
            else zero++;
        }
        return (neg, pos, zero);
    }

    private static void Print(string name, long neg, long pos, long zero, long ms,
                              long expNeg, long expPos, long expZero)
    {
        bool correct = neg == expNeg && pos == expPos && zero == expZero;
        Console.WriteLine($"{name,-26} neg={neg} pos={pos} zero={zero} | {ms} ms | " +
                          (correct ? "correct" : "WRONG"));
    }
}