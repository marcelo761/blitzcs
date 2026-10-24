using Xunit;
using BlitzCS.BinaryCompiler.Tests.Support;

namespace BlitzCS.BinaryCompiler.Tests;

public class CacheTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bcs-cache-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private static PeImage Img(byte[]? code = null) => PeReader.Read(new PeFixtureBuilder().WithCode(code ?? [0xC3]).Build());

    private static TranslationArtifact Artifact(string key = "abcdef0123456789") => new(1, "src", key, 0x10000000, 0x6000, [1, 2, 3, 4, 5, 6, 7, 8], 4, 4, 8, 16, 32, 0x7000, 0x7010,
        [new AddressMapEntry(0x1000, 0), new AddressMapEntry(0x1001, 2)],
        [new TranslatedExport("f", 1, 0x1000, 0, GuestCallingConvention.Stdcall, 8), new TranslatedExport(null, 2, 0x1001, 2, GuestCallingConvention.Unknown, null)],
        [new PeImport("k.dll", "Sleep", null, 0x3400), new PeImport("k.dll", null, 7, 0x3404)],
        [new BinaryDiagnostic(DiagnosticSeverity.Warning, "X1", "msg", 5), new BinaryDiagnostic(DiagnosticSeverity.Info, "X2", "m2")]);

    [Fact]
    public void KeyIsSensitiveToEveryInput()
    {
        var img = Img(); var o = new BinaryCompilerOptions();
        var k = CacheKey.Compute(img, o);
        Assert.Matches("^[0-9a-f]{64}$", k);
        Assert.Equal(k, CacheKey.Compute(Img(), o));
        var all = new[]
        {
            CacheKey.Compute(Img([0x90, 0xC3]), o), CacheKey.Compute(img, o with { Profile = OptimizationProfile.Debug }),
            CacheKey.Compute(img, o with { TargetArchitecture = "arm64" }), CacheKey.Compute(img, o with { TargetOs = "windows" }),
            CacheKey.Compute(img, o, ["aa"]), CacheKey.Compute(img, o, ["aa", "bb"]),
        };
        Assert.Equal(all.Length, all.Append(k).Distinct().Count() - 1);
        Assert.Equal(CacheKey.Compute(img, o, ["aa", "bb"]), CacheKey.Compute(img, o, ["bb", "aa"]));
    }

    [Fact]
    public void SerializerRoundTrips()
    {
        var a = Artifact(); var s = new ArtifactSerializer();
        var r = s.Deserialize(s.Serialize(a));
        Assert.Equal(a.HostImage, r.HostImage);
        Assert.Equal(a with { HostImage = [], AddressMap = [], Exports = [], Imports = [], Diagnostics = [] }, r with { HostImage = [], AddressMap = [], Exports = [], Imports = [], Diagnostics = [] });
        Assert.Equal(a.AddressMap, r.AddressMap); Assert.Equal(a.Exports, r.Exports); Assert.Equal(a.Imports, r.Imports); Assert.Equal(a.Diagnostics, r.Diagnostics);
    }

    [Fact]
    public void CorruptionAlwaysSpecificException()
    {
        var s = new ArtifactSerializer(); var good = s.Serialize(Artifact());
        for (var i = 0; i < good.Length; i++)
            for (var bit = 0; bit < 8; bit += 3)
            {
                var c = (byte[])good.Clone(); c[i] ^= (byte)(1 << bit);
                Assert.Throws<ArtifactFormatException>(() => s.Deserialize(c));
            }
        for (var len = 0; len < good.Length; len++)
            Assert.Throws<ArtifactFormatException>(() => s.Deserialize(good.AsSpan(0, len)));
        Assert.Throws<ArtifactFormatException>(() => s.Deserialize(good.Append((byte)0).ToArray()));
    }

    [Fact]
    public void ResignedGarbageBodiesAreRejected()
    {
        var s = new ArtifactSerializer(); var rng = new Random(1234); var good = s.Serialize(Artifact());
        for (var n = 0; n < 500; n++)
        {
            var body = good[..^32]; var c = (byte[])body.Clone();
            for (var k = 0; k < 3; k++) c[rng.Next(c.Length)] = (byte)rng.Next(256);
            var signed = c.Concat(System.Security.Cryptography.SHA256.HashData(c)).ToArray();
            try { s.Deserialize(signed); } catch (ArtifactFormatException) { }
        }
    }

    [Fact]
    public void CacheHitMissAndOverwrite()
    {
        var cache = new TranslationCache(dir); const string key = "abcdef0123456789";
        Assert.False(cache.TryGet(key, out var none)); Assert.Null(none);
        cache.Put(key, Artifact());
        Assert.True(cache.TryGet(key, out var hit)); Assert.Equal(Artifact().HostImage, hit!.HostImage);
        cache.Put(key, Artifact() with { SourceSha256 = "other" });
        Assert.True(cache.TryGet(key, out hit)); Assert.Equal("other", hit!.SourceSha256);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void CorruptEntryIsMissAndDeleted()
    {
        var cache = new TranslationCache(dir); const string key = "abcdef0123456789";
        cache.Put(key, Artifact());
        var file = Directory.GetFiles(dir).Single(); File.WriteAllBytes(file, [1, 2, 3]);
        Assert.False(cache.TryGet(key, out _)); Assert.False(File.Exists(file));
        cache.Put(key, Artifact() with { CacheKey = "ffffffffffffffff" });
        Assert.False(cache.TryGet(key, out _));
    }

    [Theory]
    [InlineData("../evil")] [InlineData("")] [InlineData("ABCDEF0123456789")] [InlineData("abc")] [InlineData("abcdef01/../../x")]
    public void InvalidKeysRejected(string key)
    {
        var cache = new TranslationCache(dir);
        Assert.Throws<ArgumentException>(() => cache.TryGet(key, out _));
        Assert.Throws<ArgumentException>(() => cache.Put(key, Artifact()));
    }

    [Fact]
    public void ConcurrentPutsAndGets()
    {
        var cache = new TranslationCache(dir); const string key = "abcdef0123456789";
        Parallel.For(0, 64, i =>
        {
            if (i % 2 == 0) cache.Put(key, Artifact());
            else if (cache.TryGet(key, out var a)) Assert.Equal(key, a!.CacheKey);
        });
        Assert.True(cache.TryGet(key, out _));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
