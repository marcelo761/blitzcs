using System.Security.Cryptography;
using BlitzCS.BinaryCompiler.CodeGen;

namespace BlitzCS.BinaryCompiler;

public sealed record BinaryBuildResult(TranslationArtifact Artifact, bool CacheHit);

/// <summary>Build-time entry point: parses, translates and caches one PE32 library. Unchanged inputs are never retranslated.</summary>
public static class BinaryBuild
{
    public static bool IsPe32X86(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[0x40];
        if (stream.Read(head) < head.Length || head[0] != 'M' || head[1] != 'Z') return false;
        var peOffset = BitConverter.ToUInt32(head[0x3C..]);
        if (peOffset > stream.Length - 6) return false;
        stream.Position = peOffset;
        Span<byte> sig = stackalloc byte[6];
        return stream.Read(sig) == 6 && sig[..4].SequenceEqual("PE\0\0"u8) && BitConverter.ToUInt16(sig[4..]) == 0x14C;
    }

    public static BinaryBuildResult Translate(string dllPath, BinaryCompilerOptions options, string cacheDirectory, IEnumerable<string>? dependencySha256 = null)
    {
        var image = PeReader.Read(File.ReadAllBytes(dllPath));
        var key = CacheKey.Compute(image, options, dependencySha256);
        var cache = new TranslationCache(cacheDirectory);
        if (cache.TryGet(key, out var hit) && hit is not null) return new(hit, true);
        var artifact = X64Translator.Translate(image, options, key);
        cache.Put(key, artifact);
        return new(artifact, false);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
