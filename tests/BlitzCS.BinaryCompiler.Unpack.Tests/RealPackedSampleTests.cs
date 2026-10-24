using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.Testing;
using Xunit;
using Xunit.Abstractions;

namespace BlitzCS.BinaryCompiler.Unpack.Tests;

/// <summary>
/// Runs against a real UPX-packed DLL named by BLITZCS_PACKED_SAMPLE (read only, never copied into the repository). The tests are reported SKIPPED, with the reason, when the
/// variable is unset or the file is missing (they never pass vacuously). The assertions are structural (reached the OEP, exports populated, derived image translates,
/// a getter returns a plausible value); no library version is hard-coded beyond the documented FSOUND_GetVersion of the sample: the observed values go to the test output.
/// On Windows the stub runs mapped at a base the host can use (UnpackOptions.LoadBase); on Linux the preferred base is exercised as well as an explicit one.
/// </summary>
[Collection("GuestRuntime")]
public sealed class RealPackedSampleTests(ITestOutputHelper output)
{
    private const uint WantedBase = 0x30000000;

    /// <summary>Linux: the image's preferred base. The derived image of the sample is fixed at 0x10000000 without relocations, which windows-x64 cannot map (see UnpackRefusalTests).</summary>
    [PackedSampleFact("on Windows the stub is run at a host-valid base instead (Real_sample_unpacked_at_a_chosen_base_runs_like_the_native_original)")]
    public void Real_sample_unpacks_translates_and_a_trivial_export_runs() => RunSample(null);

    /// <summary>All hosts: UnpackOptions.LoadBase. On Windows this is THE run of the real library (FMOD at a base above 0x20000000).</summary>
    [PackedSampleFact]
    public void Real_sample_unpacked_at_a_chosen_base_runs_like_the_native_original()
    {
        var packed = PeReader.Read(File.ReadAllBytes(RealLibraries.PackedSample!));
        // the derived image adds an import section after the last section: leave room for it
        var chosen = HostPlatform.FreeBase(WantedBase, packed.SizeOfImage + 0x80000);
        output.WriteLine($"LoadBase 0x{chosen:X8} (wanted 0x{WantedBase:X8}); packed preferred base 0x{packed.ImageBase:X8}, {packed.Relocations.Count} relocation(s) in the packed file");
        RunSample(chosen);
    }

    /// <summary>
    /// Independent evidence that the packer's own relocation processing ran: the same library unpacked mapped at two bases must differ only in 4-byte locations that hold a
    /// base-dependent address, and each such location differs by exactly the base delta. No pointer into the old range may survive in the relocated image.
    /// </summary>
    [PackedSampleFact]
    public void Real_sample_unpacked_at_two_bases_differs_only_by_relocated_addresses()
    {
        var packed = PeReader.Read(File.ReadAllBytes(RealLibraries.PackedSample!));
        const uint baseB = 0x30000000;
        var a = PeReader.Read(Unpack(packed, null)); var b = PeReader.Read(Unpack(packed, baseB));
        Assert.Equal(packed.ImageBase, a.ImageBase); Assert.Equal(baseB, b.ImageBase);
        var delta = unchecked(baseB - packed.ImageBase);
        var ma = a.MapImage(); var mb = b.MapImage();
        Assert.Equal(ma.Length, mb.Length);
        var first = (int)a.Sections.Min(s => s.VirtualAddress);
        uint At(byte[] m, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(i));
        var relocated = 0; var uncovered = new List<int>();
        for (var i = first; i < ma.Length; i++)
        {
            if (ma[i] == mb[i]) continue;
            var p = Enumerable.Range(Math.Max(first, i - 3), 4).Where(q => q + 4 <= ma.Length && unchecked(At(mb, q) - At(ma, q)) == delta).Select(q => (int?)q).FirstOrDefault();
            if (p is { } q2) { relocated++; i = q2 + 3; }
            else uncovered.Add(i);
        }
        output.WriteLine($"relocated 4-byte locations: {relocated}; differing bytes that are not a relocated address: {uncovered.Count}");
        Assert.True(relocated > 1000, $"expected the library's absolute addresses to differ ({relocated})");
        Assert.True(uncovered.Count == 0, "bytes differ for another reason than the base delta at RVA " + string.Join(", ", uncovered.Take(8).Select(x => "0x" + x.ToString("X"))));
        // Observation only: aligned dwords in non-executable data that still hold a preferred-base look-alike are constants, not pointers (the packer's own relocation list did not
        // mention them, and the unexplained-byte check above proves every base-dependent location moved by exactly the delta). The stub's own section is compressed data, skipped.
        var lookAlikes = 0;
        foreach (var sec in a.Sections.Where(x => !x.IsExecutable && x.VirtualAddress != a.Sections.First(t => t.VirtualAddress <= packed.EntryPointRva && packed.EntryPointRva < t.VirtualAddress + Math.Max(t.VirtualSize, t.RawSize)).VirtualAddress))
            for (var i = (int)sec.VirtualAddress & ~3; i + 4 <= Math.Min(mb.Length, (int)(sec.VirtualAddress + Math.Max(sec.VirtualSize, sec.RawSize))); i += 4)
            {
                var v = At(mb, i);
                if (v >= packed.ImageBase && v < packed.ImageBase + packed.SizeOfImage && At(ma, i) == v) { lookAlikes++; output.WriteLine($"constant that looks like a preferred-base pointer: RVA 0x{i:X} in {sec.Name} = 0x{v:X8}"); }
            }
        output.WriteLine($"look-alike constants in data: {lookAlikes}");

        static byte[] Unpack(PeImage image, uint? loadBase)
        {
            var r = ControlledUnpacker.TryUnpack(image, new UnpackOptions { LoadBase = loadBase });
            Assert.True(r.Success, r.Report.RefusalCode);
            return r.DerivedPe!;
        }
    }

    private void RunSample(uint? loadBase)
    {
        var sample = RealLibraries.PackedSample!;
        var original = File.ReadAllBytes(sample);
        var r = ControlledUnpacker.TryUnpack(PeReader.Read(original), new UnpackOptions { LoadBase = loadBase });
        output.WriteLine($"success={r.Success} oep={r.Report.OepRva:X} confidence={r.Report.Confidence} instructions={r.Report.Instructions} elapsed={r.Report.Elapsed} written={r.Report.BytesWrittenToImage}");
        foreach (var e in r.Report.Evidence) output.WriteLine("evidence: " + e);
        foreach (var d in r.Diagnostics) output.WriteLine($"{d.Severity} {d.Code}: {d.Message}");
        output.WriteLine($"resolved by the stub: {r.Report.ResolvedImports.Count} (called: {r.Report.ResolvedImports.Count(i => i.Called)}); rebuilt imports {r.Report.RebuiltImports} in {r.Report.RebuiltImportDescriptors} descriptor(s); code pages {r.Report.CodePages}, data pages {r.Report.DataPages}");
        Assert.True(r.Success);
        Assert.Equal(OepConfidence.High, r.Report.Confidence);
        Assert.Equal(r.Report.ExportsTotal, r.Report.ExportsPopulated);
        Assert.Equal(original, File.ReadAllBytes(sample));

        var derived = PeReader.Read(r.DerivedPe!);
        Assert.DoesNotContain(derived.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(derived.Sections, s => s.IsExecutable && s.IsWritable);
        Assert.Equal(r.Report.RebuiltImports, derived.Imports.Count);
        var options = new BinaryCompilerOptions();   // the host's target: on Windows the derived base (>= 0x20000000) is valid, so no BC_BASE_UNUSABLE_ON_TARGET
        var artifact = X64Translator.Translate(derived, options, CacheKey.Compute(derived, options));
        Assert.Equal(loadBase ?? derived.ImageBase, derived.ImageBase);
        output.WriteLine($"derived image: base 0x{derived.ImageBase:X8}, {derived.Relocations.Count} relocation(s), {derived.Imports.Count} import(s), {derived.Exports.Count} export(s)");
        foreach (var g in artifact.Diagnostics.GroupBy(d => (d.Severity, d.Code))) output.WriteLine($"translate {g.Key.Severity} {g.Key.Code} x{g.Count()}");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // The derived image has no relocation table, so function-pointer tables are found by scanning data; the speculative roots may decode into
        // data and are reported as these two warnings only.
        foreach (var w in artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning && d.Code is not ("CFG_DECODE_FAILURE" or "BC_UNTRANSLATED_TARGET")).Take(8)) output.WriteLine($"warn {w.Code} @{w.GuestRva:X}: {w.Message}");
        Assert.All(artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning), d => Assert.Contains(d.Code, new[] { "CFG_DECODE_FAILURE", "BC_UNTRANSLATED_TARGET", "BC_UNSUPPORTED_INSTRUCTION", "BC_SEGMENT_OVERRIDE" }));
        Assert.Contains(artifact.Diagnostics, d => d.Code == "CFG_POINTER_SCAN_ROOTS");

        using var resolver = new Win32ImportResolver();
        var missing = resolver.Analyze(derived).Count(v => v.Status == Win32ImportStatus.Missing);
        output.WriteLine($"imports {derived.Imports.Count}: Win32 component implements {derived.Imports.Count - missing}, missing {missing}");
        var getter = derived.Exports.FirstOrDefault(e => e.Name is { } n && n.Contains("GetVersion", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(getter);   // an FMOD 3.x DLL exports _FSOUND_GetVersion@0
        float value;
        using (var module = GuestModule.Load(artifact, derived, resolver, new GuestModuleOptions { RunEntryPoint = false, AllowUnboundImports = true }))
            value = module.Bind(getter.Name!, GuestCallingConvention.Stdcall, 0).CallSingle();
        output.WriteLine($"{getter.Name} -> {value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        Assert.True(float.IsFinite(value) && value > 0 && value < 100, "a version getter should return a small positive float");

        // With the Win32 component's msvcrt/kernel32/winmm services DllMain (the OEP) now runs to completion and returns TRUE.
        using var attachResolver = new Win32ImportResolver(); // one resolver per guest module
        using var attached = GuestModule.Load(artifact, derived, attachResolver, new GuestModuleOptions { RunEntryPoint = true, AllowUnboundImports = true });
        output.WriteLine("DllMain: returned TRUE (module loaded)");
        Assert.Equal(3.75f, attached.Bind(getter.Name!, GuestCallingConvention.Stdcall, 0).CallSingle());
        Assert.Empty(Kernel32ExtraService.ThreadFaults(attachResolver.Context));

        uint Call(string name, params uint[] args)
        {
            try { var r = attached.Bind("_FSOUND_" + name + "@" + 4 * args.Length, GuestCallingConvention.Stdcall, args.Length).Call(args); output.WriteLine($"{name}({string.Join(",", args)}) -> {(sbyte)r}"); return r; }
            catch (GuestException e) { output.WriteLine($"{name}: {e.GetType().Name}: {e.Message}"); throw; }
        }

        // State-only API. Values other than the ones asserted are observations (written to the output), not validation against FMOD documentation.
        Assert.Equal(0, (sbyte)Call("GetError"));
        Assert.Equal(0, (sbyte)Call("GetNumDrivers"));       // headless host: winmm reports no devices
        foreach (var output3 in new uint[] { 0, 1 })          // FSOUND_OUTPUT_NOSOUND = 0 and WINMM = 1 (docs/native-original-m13.md observes NOSOUND = 0)
        {
            Call("SetOutput", output3);
            Assert.Equal((sbyte)output3, (sbyte)Call("GetOutput"));
        }
        Call("SetOutput", 0);
        output.WriteLine("GetMixer observed " + (sbyte)Call("GetMixer") + "; GetOutputRate observed " + Call("GetOutputRate"));
        // FSOUND_Init / SetDriver / driver enumeration dispatch through a function-pointer table. The unpacker drops relocations, so the translator
        // finds those targets by scanning data for code pointers.
        try { Call("Init", 44100, 64, 0); output.WriteLine("Init completed"); }
        catch (GuestException e) { output.WriteLine("FSOUND_Init: " + e.GetType().Name + ": " + e.Message); throw; }
        Assert.Equal(0u, Call("GetError") & 0xFF);                   // no error recorded after a successful NOSOUND initialisation
        Assert.Equal(0, (sbyte)Call("GetOutput"));                   // NOSOUND stays selected
        // State-only getters after initialisation: observations, not validation against the FMOD documentation.
        foreach (var getter2 in new[] { "GetMaxChannels", "GetChannelsPlaying", "GetNumHWChannels", "GetCPUUsage", "GetSFXMasterVolume" })
            try { Call(getter2); } catch (GuestException e) { output.WriteLine($"{getter2}: {e.GetType().Name}"); }
        // A guest worker thread that dies leaves its waiters spinning on Windows too; fail fast with a clear message rather than hang the run.
        var finished = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (!finished.Wait(TimeSpan.FromSeconds(30)))
                Environment.FailFast("FSOUND_Close did not return; thread faults: " + string.Join(" | ", Kernel32ExtraService.ThreadFaults(attachResolver.Context).Select(f => f.Message)));
        }) { IsBackground = true };
        watchdog.Start();
        Call("Close");
        finished.Set();
        Assert.Empty(Kernel32ExtraService.ThreadFaults(attachResolver.Context));
        Assert.Equal(0u, Call("GetError") & 0xFF);
    }
}
