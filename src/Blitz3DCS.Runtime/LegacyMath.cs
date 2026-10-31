namespace Blitz3DCS.Runtime;

/// <summary>Source-compatible scalar math commands from the original Blitz3D runtime.</summary>
/// <remarks>The random generator is process-global, matching the native runtime's state.</remarks>
public static class LegacyMath
{
    private const float DegreesToRadians = 0.0174532925199432957692369076848861f;
    private const double RadiansToDegrees = 57.2957795130823208767981548141052;
    private const int RandomA = 48271;
    private const int RandomM = 2147483647;
    private const int RandomQ = 44488;
    private const int RandomR = 3399;
    private static int _randomState = 0x1234;

    public static float Sin(float degrees) => (float)Math.Sin(degrees * DegreesToRadians);
    public static float Cos(float degrees) => (float)Math.Cos(degrees * DegreesToRadians);
    public static float Floor(float value) => (float)Math.Floor(value);
    public static float Ceil(float value) => (float)Math.Ceiling(value);
    public static float Sqr(float value) => (float)Math.Sqrt(value);
    public static float ATan2(float y, float x) => (float)(Math.Atan2(y, x) * RadiansToDegrees);

    public static float Rnd(float from, float to = 0) => NextRandom() * (to - from) + from;

    public static int Rand(int from, int to = 1)
    {
        if (to < from) (from, to) = (to, from);
        var range = (long)to - from + 1;
        return (int)(NextRandom() * range) + from;
    }

    public static void SeedRnd(int seed)
    {
        seed &= 0x7fffffff;
        _randomState = seed == 0 ? 1 : seed;
    }

    public static int RndSeed() => _randomState;

    private static float NextRandom()
    {
        _randomState = RandomA * (_randomState % RandomQ) - RandomR * (_randomState / RandomQ);
        if (_randomState < 0) _randomState += RandomM;
        return (_randomState & 65535) / 65536f + (0.5f / 65536f);
    }
}
