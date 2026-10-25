using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlitzCS.BinaryCompiler.Unpack;

public sealed class UnpackFailedException(UnpackResult result)
    : InvalidOperationException("Unpacking failed: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code}: {d.Message}")))
{
    public UnpackResult Result { get; } = result;
}

public sealed record UnpackBuildResult(BinaryBuildResult Build, PeImage DerivedImage, string DerivedPath, UnpackLineage Lineage, bool UnpackCacheHit);

/// <summary>Unpack-then-translate. <see cref="BinaryBuild.Translate"/> is unchanged; this is the explicit opt-in path.</summary>
public static class UnpackBuild
{
    /// <summary>
    /// Derived images live in <c>cacheDirectory/unpacked/&lt;lineage key&gt;.dll</c> with a <c>.lineage.json</c> sidecar (derived-from SHA-256, unpacker version, options digest,
    /// derived SHA-256). A matching, intact derived file is reused without re-running the stub. The translation cache key additionally includes the lineage token.
    /// </summary>
    public static UnpackBuildResult TranslateWithUnpack(string dllPath, BinaryCompilerOptions options, string cacheDirectory, UnpackOptions? unpack = null, IEnumerable<string>? dependencySha256 = null)
    {
        var (derivedPath, lineage, hit) = Prepare(dllPath, cacheDirectory, unpack ?? new UnpackOptions());
        var build = BinaryBuild.Translate(derivedPath, options, cacheDirectory, (dependencySha256 ?? []).Append(lineage.Token));
        return new UnpackBuildResult(build, PeReader.Read(File.ReadAllBytes(derivedPath)), derivedPath, lineage, hit);
    }

    public static (string DerivedPath, UnpackLineage Lineage, bool CacheHit) Prepare(string dllPath, string directory, UnpackOptions unpack)
    {
        var bytes = File.ReadAllBytes(dllPath);
        var originalSha = BinaryBuild.Sha256(bytes);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{originalSha}|{ControlledUnpacker.Version}|{unpack.Digest()}|{BinaryCompilerOptions.CompilerVersion}"))).ToLowerInvariant()[..32];
        var dir = Path.Combine(directory, "unpacked");
        Directory.CreateDirectory(dir);
        var derived = Path.Combine(dir, key + ".dll");
        var sidecar = Path.Combine(dir, key + ".lineage.json");
        if (File.Exists(derived) && File.Exists(sidecar))
        {
            try
            {
                var l = JsonSerializer.Deserialize<UnpackLineage>(File.ReadAllText(sidecar));
                if (l is not null && l.DerivedFromSha256 == originalSha && l.UnpackerVersion == ControlledUnpacker.Version && l.OptionsDigest == unpack.Digest()
                    && BinaryBuild.Sha256(File.ReadAllBytes(derived)) == l.DerivedSha256)
                    return (derived, l, true);
            }
            catch (JsonException) { /* corrupt sidecar: unpack again */ }
        }
        var result = ControlledUnpacker.TryUnpack(PeReader.Read(bytes), unpack);
        if (!result.Success) throw new UnpackFailedException(result);
        var tmp = derived + ".tmp" + Environment.ProcessId;
        File.WriteAllBytes(tmp, result.DerivedPe!);
        File.Move(tmp, derived, true);
        File.WriteAllText(sidecar, JsonSerializer.Serialize(result.Lineage));
        return (derived, result.Lineage!, false);
    }
}

/// <summary>Catalog hook: <c>"unpack": true</c> selects this preprocessor (loaded by name from the Unpack assembly only on request).</summary>
public sealed class UnpackPreprocessor(UnpackOptions? options = null) : IBinaryPreprocessor
{
    public string Id => "unpack";

    public BinaryPreprocessResult Preprocess(string inputPath, string workDirectory) => Preprocess(inputPath, workDirectory, null);

    /// <summary>The stub runs mapped at <paramref name="loadBase"/> (null: the options' own LoadBase, else the preferred base); the derived image is fixed at that base.</summary>
    public BinaryPreprocessResult Preprocess(string inputPath, string workDirectory, uint? loadBase)
    {
        var unpack = options ?? new UnpackOptions();
        if (loadBase is not null) unpack = unpack with { LoadBase = loadBase };
        var (path, lineage, hit) = UnpackBuild.Prepare(inputPath, workDirectory, unpack);
        return new BinaryPreprocessResult(path, lineage.Token,
            [new BinaryDiagnostic(DiagnosticSeverity.Info, "UNPACK_PREPROCESSED", $"Library unpacked into a derived image ({(hit ? "reused" : "new")}, derived from {lineage.DerivedFromSha256[..16]}).")]);
    }
}
