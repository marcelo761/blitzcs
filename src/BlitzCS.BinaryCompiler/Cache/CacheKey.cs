using System.Security.Cryptography;
using System.Text;

namespace BlitzCS.BinaryCompiler;

public static class CacheKey
{
    public static string Compute(PeImage image, BinaryCompilerOptions options, IEnumerable<string>? dependencySha256 = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write("blitzcs-binary-cache"); w.Write(ArtifactSerializer.CurrentVersion);
            w.Write(image.Sha256); w.Write(BinaryCompilerOptions.CompilerVersion);
            w.Write(options.TargetArchitecture); w.Write(options.TargetOs); w.Write((int)options.Profile);
            // Rebasing changes every embedded absolute address, so the (original base, target base) pair is key material. It is written only
            // when they differ, which keeps keys of images translated at their preferred base unchanged.
            var originalBase = image.RebasedFrom?.OriginalImageBase ?? image.ImageBase;
            var targetBase = options.LoadBase ?? image.ImageBase;
            if (originalBase != targetBase) { w.Write("rebase"); w.Write(originalBase); w.Write(targetBase); }
            var deps = (dependencySha256 ?? []).Select(d => d.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            w.Write(deps.Count);
            foreach (var d in deps) w.Write(d);
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }
}
