using BlitzCS.BinaryCompiler.CodeGen;
using System.Text;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Tests.Support;

/// <summary>Optional deliberate corruption of the fixture (used to prove the harness can fail).</summary>
public sealed record FixtureMutation(int CodeOffset = -1, byte CodeXor = 0, int DataOffset = -1, byte DataXor = 0);

public sealed record DifferentialOutcome(string Lib, OptimizationProfile Profile, int Vectors, string? Unsupported, string? Divergence)
{
    public bool Passed => Unsupported is null && Divergence is null;
    public override string ToString() => Unsupported ?? Divergence ?? $"{Lib}/{Profile}: {Vectors} vectors identical to the x86 oracle";
}

/// <summary>Replays the oracle's exact vectors on the translated guest and compares results, data bytes and callback traces.</summary>
internal static class OracleDifferential
{
    public const uint DataVa = TranslatorHarness.DataVa;

    /// <summary>The imports a library may call, with the same formulas as the oracle driver's host functions.</summary>
    public static readonly (string Dll, string Name)[] CallbackImports = [("host.dll", "cb_std"), ("host.dll", "cb_c")];

    public static byte[] BuildFixture(OracleArtifacts a, FixtureMutation? mutation = null)
    {
        var code = (byte[])a.Code.Clone(); var data = a.Data.Length >= 16 ? (byte[])a.Data.Clone() : new byte[0x100];
        if (mutation is { CodeOffset: >= 0 } m) code[m.CodeOffset] ^= m.CodeXor;
        if (mutation is { DataOffset: >= 0 } d) data[d.DataOffset] ^= d.DataXor;
        var fx = new PeFixtureBuilder().WithCode(code).WithData(data);
        foreach (var (name, off) in a.Exports) fx.Export(name, off);
        if (a.Name == "cb") foreach (var (dll, name) in CallbackImports) fx.Import(dll, name);
        foreach (var r in a.Relocs) fx.Relocate(r);
        return fx.Build();
    }

    public static DifferentialOutcome Run(string lib, OptimizationProfile profile, FixtureMutation? mutation = null, OracleTranscript? oracleOverride = null)
    {
        var art = OracleRunner.Build(lib);
        var oracle = oracleOverride ?? OracleRunner.Run(lib);
        var image = PeReader.Read(BuildFixture(art, mutation));
        var tr = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "diff-" + lib);
        var errors = tr.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            return new(lib, profile, 0, $"{FailureClass.Unsupported}: {lib}/{profile}: translator reported {errors.Count} error diagnostic(s): " +
                string.Join("; ", errors.Take(4).Select(e => $"{e.Code}@{e.GuestRva:X}: {e.Message}")), null);

        var trace = new List<string>();
        var resolver = new DictionaryGuestImportResolver()
            .Add("host.dll", "cb_std", GuestImportBuilder.Stdcall((a, b) => { var r = unchecked(a * 31u + (b ^ 0x55u) + 7u); trace.Add($"cb_std {a:x} {b:x} 0 {r:x}"); return r; }))
            .Add("host.dll", "cb_c", GuestImportBuilder.Cdecl((a, b, c) => { var r = unchecked((a ^ b) + c * 3u); trace.Add($"cb_c {a:x} {b:x} {c:x} {r:x}"); return r; }));
        using var module = GuestModule.Load(tr, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        var fns = oracle.Functions.ToDictionary(f => f.Name, f => module.Bind(f.Name, f.Convention, f.ArgCount));
        var buf = new byte[oracle.DataLength];

        uint Hash() { module.Memory.Read(DataVa, buf); return Fnv(buf); }
        var pfx = $"{FailureClass.Translation}: {lib}/{profile}";
        if (Hash() != oracle.InitialDataHash) return new(lib, profile, 0, null, $"{pfx}: initial data section differs from the oracle's before any call");
        foreach (var v in oracle.Vectors)
        {
            trace.Clear();
            var ret = fns[v.Function].Call(v.Args);
            var args = string.Join(", ", v.Args.Select(x => "0x" + x.ToString("x")));
            string Where() => $"{pfx}: first divergence at vector #{v.Index} {v.Function}({args})";
            if (ret != v.Result) return new(lib, profile, v.Index, null, $"{Where()}: return oracle=0x{v.Result:x} guest=0x{ret:x}");
            var n = Math.Max(trace.Count, v.Trace.Length);
            for (var i = 0; i < n; i++)
                if (i >= trace.Count || i >= v.Trace.Length || trace[i] != v.Trace[i])
                    return new(lib, profile, v.Index, null, $"{Where()}: callback trace[{i}] oracle='{(i < v.Trace.Length ? v.Trace[i] : "<none>")}' guest='{(i < trace.Count ? trace[i] : "<none>")}' (counts {v.Trace.Length} vs {trace.Count})");
            var h = Hash();
            if (h != v.DataHash) return new(lib, profile, v.Index, null, $"{Where()}: data-section hash oracle=0x{v.DataHash:x} guest=0x{h:x} (return value matched)");
        }
        Hash();
        for (var i = 0; i < buf.Length; i++)
            if (buf[i] != oracle.FinalData[i])
                return new(lib, profile, oracle.Vectors.Count, null, $"{pfx}: final data differs at offset 0x{i:x} (VA 0x{DataVa + (uint)i:x}) oracle=0x{oracle.FinalData[i]:x2} guest=0x{buf[i]:x2}");
        return new(lib, profile, oracle.Vectors.Count, null, null);
    }

    public static uint Fnv(ReadOnlySpan<byte> data) { var h = 2166136261u; foreach (var b in data) h = unchecked((h ^ b) * 16777619u); return h; }

    public static string Describe(IEnumerable<DifferentialOutcome> o) { var sb = new StringBuilder(); foreach (var x in o) sb.AppendLine(x.ToString()); return sb.ToString(); }
}
