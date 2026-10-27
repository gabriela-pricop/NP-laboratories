namespace Lab1.Task2;

public readonly record struct Counts(long Negative, long Positive, long Zero)
{
    public long Total => Negative + Positive + Zero;

    public static Counts operator +(Counts a, Counts b) =>
        new(a.Negative + b.Negative, a.Positive + b.Positive, a.Zero + b.Zero);
}