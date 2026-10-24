using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.Testing;
using Xunit;
using Xunit.Abstractions;

namespace BlitzCS.BinaryCompiler.Unpack.Tests;

/// <summary>
/// F-13 regression on the real packed FMOD (BLITZCS_PACKED_SAMPLE; reported SKIPPED with the reason when unset). FSOUND_SetMixer(FSOUND_MIXER_QUALITY_FPU = 5) used to kill the process;
/// the root cause (code dwords taken for import slots) is fixed in the unpacker, and what remains is that the FPU mixer asks kernel32!VirtualProtect to make its own code writable.
/// That is self-modifying code, which ahead-of-time translation cannot follow, so the call must fail by name and leave the process, and every other module, usable.
/// The audio runs on the virtual sink, so no device is touched.
/// </summary>
[Collection("GuestRuntime")]
public sealed class RealPackedFpuMixerTests(ITestOutputHelper output)
{
    private const int MixerQualityFpu = 5, MixerAutodetect = 0, OutputWinmm = 1;

    private static readonly Lazy<(PeImage Image, TranslationArtifact Artifact, string Getter)> Fmod = new(() =>
    {
        var packed = PeReader.Read(File.ReadAllBytes(RealLibraries.PackedSample!));
        var chosen = HostPlatform.FreeBase(0x30000000, packed.SizeOfImage + 0x80000);
        var r = ControlledUnpacker.TryUnpack(packed, new UnpackOptions { LoadBase = chosen });
        Assert.True(r.Success, r.Report.RefusalCode);
        var derived = PeReader.Read(r.DerivedPe!);
        var options = new BinaryCompilerOptions();
        var artifact = X64Translator.Translate(derived, options, CacheKey.Compute(derived, options));
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var getter = derived.Exports.First(e => e.Name is { } n && n.Contains("GetVersion", StringComparison.OrdinalIgnoreCase)).Name!;
        return (derived, artifact, getter);
    });

    /// <summary>One loaded FMOD with its own Win32 environment writing to the virtual sink; <see cref="Writes"/> counts the buffers the mixer handed to waveOut.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Win32ImportResolver resolver;
        private readonly GuestModule module;
        private readonly string getter;
        private readonly ITestOutputHelper output;
        private int writes;
        public int Writes => Volatile.Read(ref writes);
        public Win32Context Context => resolver.Context;

        public Rig(ITestOutputHelper output)
        {
            this.output = output;
            var (image, artifact, getter) = Fmod.Value;
            this.getter = getter;
            resolver = new Win32ImportResolver(new Win32Options { WaveOut = WaveOutBackendKind.VirtualSink, WaveOutObserver = _ => Interlocked.Increment(ref writes) });
            module = GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { RunEntryPoint = true, AllowUnboundImports = true });
        }

        public float Version() => module.Bind(getter, GuestCallingConvention.Stdcall, 0).CallSingle();

        public uint Call(string name, params uint[] args)
        {
            var r = module.Bind("_FSOUND_" + name + "@" + 4 * args.Length, GuestCallingConvention.Stdcall, args.Length).Call(args);
            output.WriteLine($"{name}({string.Join(",", args.Select(a => (int)a))}) -> {(int)r}");
            return r;
        }

        public void Dispose() { module.Dispose(); resolver.Dispose(); }
    }

    [PackedSampleFact]
    public void The_fpu_mixer_either_initialises_or_is_refused_by_name_and_the_process_stays_usable()
    {
        bool initialised;
        using (var rig = new Rig(output))
        {
            Assert.Equal(3.75f, rig.Version());
            rig.Call("SetOutput", OutputWinmm);
            rig.Call("SetMixer", MixerQualityFpu);
            try
            {
                var ok = rig.Call("Init", 44100, 32, 0);
                initialised = true;
                output.WriteLine("FSOUND_Init with the FPU mixer returned " + ok + ": the mixer did not need to write its code");
                Assert.NotEqual(0u, ok & 0xFF);
                rig.Call("Close");
            }
            catch (GuestException e)
            {
                initialised = false;
                output.WriteLine($"{e.GetType().Name}: {e.Message}");
                // The only acceptable failure is the named refusal of self-modifying code; an unrelated exception (fault, trap, other import) fails the test.
                var import = Assert.IsType<GuestImportException>(e);
                var smc = Assert.IsType<Win32SelfModifyingCodeException>(import.InnerException);
                Assert.Contains("VirtualProtect", import.Message);
                Assert.Contains("self-modifying code", import.Message);
                Assert.Contains("ahead-of-time translation cannot honour", smc.Message);
                Assert.Contains("translated module at base 0x", smc.Message);
                Assert.True(smc.RequestedProtection is 0x40 or 0x04 or 0x80 or 0x08, "requested protection 0x" + smc.RequestedProtection.ToString("X"));
            }
            Assert.Empty(Kernel32ExtraService.ThreadFaults(rig.Context));
        }
        output.WriteLine("FPU mixer initialised: " + initialised);

        // The process is alive and a fresh module loads and works afterwards.
        using var fresh = new Rig(output);
        Assert.Equal(3.75f, fresh.Version());
    }

    [PackedSampleFact]
    public void The_default_mixer_still_initialises_plays_through_the_virtual_sink_and_closes()
    {
        foreach (var mixer in new int?[] { null, 7 })   // untouched (autodetect) and FSOUND_MIXER_QUALITY_MMXP6
        {
            using var rig = new Rig(output);
            Assert.Equal(3.75f, rig.Version());
            rig.Call("SetOutput", OutputWinmm);
            if (mixer is { } m) rig.Call("SetMixer", (uint)m);
            var init = rig.Call("Init", 44100, 32, 0);
            Assert.NotEqual(0u, init & 0xFF);
            try
            {
                // a short silent sample played on the first free channel; the mixer thread then keeps feeding waveOut
                var sample = rig.Call("Sample_Alloc", uint.MaxValue, 4410, 0x131, 44100, 255, 128, 128);
                Assert.NotEqual(0u, sample);
                rig.Call("PlaySound", uint.MaxValue, sample);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (rig.Writes == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
                output.WriteLine($"mixer {(mixer?.ToString() ?? "default")}: {rig.Writes} buffer(s) reached the virtual sink");
                Assert.True(rig.Writes > 0, "the mixer never wrote a buffer to the virtual sink");
            }
            finally { rig.Call("Close"); }
            Assert.Empty(Kernel32ExtraService.ThreadFaults(rig.Context));
        }
    }
}
