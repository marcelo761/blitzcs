namespace BlitzCS.BinaryCompiler.Bench;

internal sealed record Dist(int N, double Median, double P10, double P90, double Min, double Max, double Mean, double StdDev)
{
    public static Dist Of(IReadOnlyList<double> xs)
    {
        var s = xs.Order().ToArray();
        double Q(double p) { var pos = p * (s.Length - 1); var lo = (int)Math.Floor(pos); var hi = Math.Min(lo + 1, s.Length - 1); return s[lo] + (s[hi] - s[lo]) * (pos - lo); }
        var mean = s.Average();
        var sd = s.Length > 1 ? Math.Sqrt(s.Sum(x => (x - mean) * (x - mean)) / (s.Length - 1)) : 0;
        return new(s.Length, Q(0.5), Q(0.1), Q(0.9), s[0], s[^1], mean, sd);
    }
}
