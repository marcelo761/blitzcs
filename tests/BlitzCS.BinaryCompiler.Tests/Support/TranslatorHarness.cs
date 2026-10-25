using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using Iced.Intel;

namespace BlitzCS.BinaryCompiler.Tests.Support;

/// <summary>Assembles IA-32 with Iced, wraps it into a fixture DLL, translates it and optionally executes it.</summary>
internal static class TranslatorHarness
{
    public static readonly uint CodeVa = PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva;
    public static readonly uint DataVa = PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva;

    public static byte[] Asm(Action<Assembler> body, out AssemblerResult result)
    {
        var a = new Assembler(32);
        body(a);
        using var ms = new MemoryStream();
        result = a.Assemble(new StreamCodeWriter(ms), CodeVa, BlockEncoderOptions.ReturnNewInstructionOffsets);
        return ms.ToArray();
    }

    public static byte[] Asm(Action<Assembler> body) => Asm(body, out _);

    public sealed record Built(PeImage Image, TranslationArtifact Artifact);

    public static Built Translate(byte[] code, OptimizationProfile profile, Action<PeFixtureBuilder>? configure = null, bool exportF = true, byte[]? data = null)
    {
        var fx = new PeFixtureBuilder().WithCode(code);
        if (exportF) fx.Export("_f", 0);
        if (data is not null) fx.WithData(data);
        configure?.Invoke(fx);
        var image = PeReader.Read(fx.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "test-key");
        return new Built(image, art);
    }

    public static readonly OptimizationProfile[] Profiles = [OptimizationProfile.Debug, OptimizationProfile.Release];

    /// <summary>Runs export _f under both profiles and asserts both agree.</summary>
    public static uint Run(Action<Assembler> body, uint[]? args = null, Action<PeFixtureBuilder>? configure = null, byte[]? data = null,
        IGuestImportResolver? imports = null, Action<GuestModule>? inspect = null)
        => Run(Asm(body), args, configure, data, imports, inspect);

    public static uint Run(byte[] code, uint[]? args = null, Action<PeFixtureBuilder>? configure = null, byte[]? data = null,
        IGuestImportResolver? imports = null, Action<GuestModule>? inspect = null)
    {
        args ??= [];
        uint? first = null;
        foreach (var profile in Profiles)
        {
            var b = Translate(code, profile, configure, true, data);
            var errors = b.Artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Xunit.Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Message)));
            using var m = GuestModule.Load(b.Artifact, b.Image, imports ?? new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
            var fn = m.Bind("_f", GuestCallingConvention.Cdecl, args.Length);
            var r = fn.Call(args);
            inspect?.Invoke(m);
            if (first is null) first = r; else Xunit.Assert.True(first == r, $"Debug/Release results differ: {first:X} vs {r:X}");
        }
        return first!.Value;
    }
}
