using System.Diagnostics;
using System.Text;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// ogg.dll, vorbis.dll and vorbisFile.dll (genuine MSVC builds; all three prefer ImageBase 0x10000000) loaded together at allocator-chosen, translation-time
/// rebased bases and linked through the module bridge. Expected values come from the Vorbis I specification (header bytes encoded here) and from ffmpeg
/// decoding the same stream, never from the translator. Binaries are read from BLITZCS_REAL_PE32_DIR and never copied.
/// Set BLITZCS_MUTATE=&lt;key&gt; (or *) to perturb the reference of one assertion and prove the test notices.
/// </summary>
[Collection("GuestRuntime")]
public sealed class RealMultiModuleTests
{
    // ---- mutation hook (reference perturbation) ----
    private static bool Mutated(string key) => Environment.GetEnvironmentVariable("BLITZCS_MUTATE") is { } m && (m == "*" || m == key);
    private static uint Ref(string key, uint expected) => Mutated(key) ? expected + 1 : expected;
    private static ulong Ref(string key, ulong expected) => Mutated(key) ? expected + 1 : expected;
    private static string Ref(string key, string expected) => Mutated(key) ? expected + "!" : expected;
    private static double Ref(string key, double expected) => Mutated(key) ? expected * 1.01 + 1e-3 : expected;

    // ---- loading ----
    private static readonly Dictionary<string, (PeImage Image, uint Base, TranslationArtifact Artifact)> Cache = [];
    private static readonly object Gate = new();

    private static string? Dir => Environment.GetEnvironmentVariable("BLITZCS_REAL_PE32_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Loaded(GuestModuleSet set, IReadOnlyList<string> names, IReadOnlyList<uint> bases) : IDisposable
    {
        public GuestModuleSet Set { get; } = set;
        public IReadOnlyList<uint> Bases { get; } = bases;
        public GuestModule this[string name] => Set[name];
        public void Dispose() => Set.Dispose();
        public IReadOnlyList<string> Names { get; } = names;
    }

    /// <summary>Loads the named libraries (in this order) at distinct bases. <paramref name="hostFor"/> may add host callbacks in front of the Win32 component.</summary>
    private static Loaded? Open(string[] names, Func<string, IGuestImportResolver?>? hostFor = null)
    {
        if (Dir is not { } dir || names.Any(n => !File.Exists(Path.Combine(dir, n + ".dll")))) return null;
        lock (Gate)
        {
            var images = names.Select(n => (n, PeReader.Read(File.ReadAllBytes(Path.Combine(dir, n + ".dll"))))).ToList();
            var plan = ModuleBaseAllocator.Allocate(images);
            var set = new GuestModuleSet(name =>
            {
                var win = new Win32ImportResolver();
                return hostFor?.Invoke(name) is { } front ? new ChainedGuestImportResolver(front, win) : win;
            });
            // eax/ogg/vorbis are exports-only C libraries without a .def: every undecorated export is cdecl (checked in the tests below)
            for (var i = 0; i < names.Length; i++)
            {
                var key = names[i] + "@" + plan[i].Base.ToString("X8");
                if (!Cache.TryGetValue(key, out var t))
                {
                    var art = X64Translator.Translate(images[i].Item2, new BinaryCompilerOptions(OptimizationProfile.Release, LoadBase: plan[i].Base), "real-" + key);
                    Assert.DoesNotContain(art.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
                    t = Cache[key] = (plan[i].Moved || images[i].Item2.ImageBase != plan[i].Base ? images[i].Item2.Rebase(plan[i].Base) : images[i].Item2, plan[i].Base, art);
                }
                set.Add(names[i], t.Artifact, t.Image, ExportConventions.AllCdecl, new GuestModuleOptions { AllowUnboundImports = true });
            }
            set.LoadAll();
            return new Loaded(set, names, plan.Select(p => p.Base).ToList());
        }
    }

    // ---- structures (32-bit layouts of libogg/libvorbis 1.0) ----
    private static byte[] Packet(uint ptr, int bytes, bool bos)
    {
        var b = new byte[32];
        BitConverter.GetBytes(ptr).CopyTo(b, 0); BitConverter.GetBytes(bytes).CopyTo(b, 4); BitConverter.GetBytes(bos ? 1 : 0).CopyTo(b, 8);
        return b;
    }

    private static byte[] IdentificationHeader(int version = 0, int channels = 2, int rate = 44100, int upper = 192000, int nominal = 128000, int lower = 64000,
        int log2Short = 8, int log2Long = 11, byte framing = 1, string magic = "vorbis")
    {
        var p = new List<byte> { 0x01 };
        p.AddRange(Encoding.ASCII.GetBytes(magic));
        p.AddRange(BitConverter.GetBytes(version)); p.Add((byte)channels); p.AddRange(BitConverter.GetBytes(rate));
        p.AddRange(BitConverter.GetBytes(upper)); p.AddRange(BitConverter.GetBytes(nominal)); p.AddRange(BitConverter.GetBytes(lower));
        p.Add((byte)(log2Short | log2Long << 4)); p.Add(framing);
        return [.. p];
    }

    private static byte[] CommentHeader(string vendor, params string[] comments)
    {
        var p = new List<byte> { 0x03 };
        p.AddRange("vorbis"u8.ToArray());
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); p.AddRange(BitConverter.GetBytes(b.Length)); p.AddRange(b); }
        Str(vendor); p.AddRange(BitConverter.GetBytes(comments.Length));
        foreach (var c in comments) Str(c);
        p.Add(1);
        return [.. p];
    }

    private sealed class VorbisHandle(GuestModule vorbis) : IDisposable
    {
        public readonly GuestBuffer Info = vorbis.Memory.Allocate(64), Comment = vorbis.Memory.Allocate(32), Op = vorbis.Memory.Allocate(32);
        private GuestBuffer? data;
        public GuestFunction Fn(string n, int args) => vorbis.Bind(n, GuestCallingConvention.Cdecl, args);
        public int Headerin(byte[] packet, bool bos)
        {
            data?.Dispose(); data = vorbis.Memory.Allocate(packet.Length); data.Write(0, packet);
            Op.Write(0, Packet(data.Address, packet.Length, bos));
            return (int)Fn("vorbis_synthesis_headerin", 3).Call(Info.Address, Comment.Address, Op.Address);
        }
        public uint InfoWord(int index) => vorbis.Memory.ReadUInt32(Info.Address + 4u * (uint)index);
        public void Dispose() { Info.Dispose(); Comment.Dispose(); Op.Dispose(); data?.Dispose(); }
    }

    [Fact]
    public void OggAndVorbisLoadTogetherAtDifferentBases()
    {
        if (Open(["ogg", "vorbis"]) is not { } l) return;
        using var _ = l;
        Assert.NotEqual(l.Bases[0], l.Bases[1]);                       // both prefer 0x10000000: one had to move
        Assert.Contains(l.Bases, b => b != 0x10000000u);
        Assert.Equal(l.Bases[0], l["ogg"].ImageBase); Assert.Equal(l.Bases[1], l["vorbis"].ImageBase);
        // the CRT start-up (DllMain) of both ran with their own Win32 state; their exports work in their own address space
        Assert.Equal(0u, l["ogg"].Bind("oggpack_bytes", GuestCallingConvention.Cdecl, 1).Call(AllocZero(l["ogg"], 32).Address));
        Assert.Throws<InvalidOperationException>(() => l["ogg"].Dispose());   // vorbis imports from ogg: cannot be disposed first
    }

    private static GuestBuffer AllocZero(GuestModule m, int n) { var b = m.Memory.Allocate(n); b.Write(0, new byte[n]); return b; }

    [Fact]
    public void VorbisIdentificationHeaderIsParsedThroughOggpackAcrossModules()
    {
        if (Open(["ogg", "vorbis"]) is not { } l) return;
        using var _ = l;
        using var v = new VorbisHandle(l["vorbis"]);
        v.Fn("vorbis_info_init", 1).Call(v.Info.Address);
        v.Fn("vorbis_comment_init", 1).Call(v.Comment.Address);

        Assert.Equal(0, v.Headerin(IdentificationHeader(), bos: true));
        // fields we encoded (Vorbis I spec 4.2.2): version, channels, rate, bitrates; codec_setup holds the blocksizes
        Assert.Equal(Ref("version", 0u), v.InfoWord(0));
        Assert.Equal(Ref("channels", 2u), v.InfoWord(1));
        Assert.Equal(Ref("rate", 44100u), v.InfoWord(2));
        Assert.Equal(Ref("bitrate_upper", 192000u), v.InfoWord(3));
        Assert.Equal(Ref("bitrate_nominal", 128000u), v.InfoWord(4));
        Assert.Equal(Ref("bitrate_lower", 64000u), v.InfoWord(5));
        var blocksize = v.Fn("vorbis_info_blocksize", 2);
        Assert.Equal(Ref("blocksize0", 1u << 8), blocksize.Call(v.Info.Address, 0));
        Assert.Equal(Ref("blocksize1", 1u << 11), blocksize.Call(v.Info.Address, 1));
        // the bit reads really crossed into ogg.dll
        var calls = l.Set.CrossModuleCalls;
        Assert.True(calls["vorbis->ogg!oggpack_readinit"] >= 1, "headerin must call oggpack_readinit in ogg.dll");
        Assert.True(calls["vorbis->ogg!oggpack_read"] >= 10, $"headerin must read the header fields with oggpack_read in ogg.dll ({calls["vorbis->ogg!oggpack_read"]} calls)");
        v.Fn("vorbis_info_clear", 1).Call(v.Info.Address);
    }

    [Theory]
    [InlineData("bad-magic", -132)]       // OV_ENOTVORBIS
    [InlineData("version-1", -134)]       // OV_EVERSION
    [InlineData("zero-channels", -133)]   // OV_EBADHEADER
    [InlineData("long-below-short", -133)]
    [InlineData("framing-zero", -133)]
    public void MalformedIdentificationHeadersAreRejectedWithTheSpecifiedErrors(string flaw, int expected)
    {
        if (Open(["ogg", "vorbis"]) is not { } l) return;
        using var _ = l;
        using var v = new VorbisHandle(l["vorbis"]);
        v.Fn("vorbis_info_init", 1).Call(v.Info.Address); v.Fn("vorbis_comment_init", 1).Call(v.Comment.Address);
        var packet = flaw switch
        {
            "bad-magic" => IdentificationHeader(magic: "vorbiz"),
            "version-1" => IdentificationHeader(version: 1),
            "zero-channels" => IdentificationHeader(channels: 0),
            "long-below-short" => IdentificationHeader(log2Short: 11, log2Long: 8),
            _ => IdentificationHeader(framing: 0),
        };
        Assert.Equal(Mutated("reject-" + flaw) ? expected + 1 : expected, v.Headerin(packet, bos: true));
    }

    [Fact]
    public void CommentHeaderIsParsedAndQueriedAcrossModules()
    {
        if (Open(["ogg", "vorbis"]) is not { } l) return;
        using var _ = l;
        var vorbis = l["vorbis"];
        using var v = new VorbisHandle(vorbis);
        v.Fn("vorbis_info_init", 1).Call(v.Info.Address); v.Fn("vorbis_comment_init", 1).Call(v.Comment.Address);
        Assert.Equal(0, v.Headerin(IdentificationHeader(), bos: true));
        Assert.Equal(0, v.Headerin(CommentHeader("BlitzCS test vendor", "TITLE=Zeta Reticuli", "ARTIST=Alpha", "ARTIST=Beta", "EMPTY="), bos: false));

        Assert.Equal(Ref("comment-count", 4u), vorbis.Memory.ReadUInt32(v.Comment.Address + 8));
        Assert.Equal(Ref("vendor", "BlitzCS test vendor"), vorbis.Memory.ReadAnsiString(vorbis.Memory.ReadUInt32(v.Comment.Address + 12)));
        var list = vorbis.Memory.ReadUInt32(v.Comment.Address); var lengths = vorbis.Memory.ReadUInt32(v.Comment.Address + 4);
        var got = Enumerable.Range(0, 4).Select(i => vorbis.Memory.ReadAnsiString(vorbis.Memory.ReadUInt32(list + 4u * (uint)i))).ToArray();
        Assert.Equal([Ref("c0", "TITLE=Zeta Reticuli"), "ARTIST=Alpha", "ARTIST=Beta", "EMPTY="], got);
        Assert.Equal(Ref("len0", 19u), vorbis.Memory.ReadUInt32(lengths));

        using var title = AllocText(vorbis, "title"); using var artist = AllocText(vorbis, "ARTIST"); using var missing = AllocText(vorbis, "GENRE");
        var query = v.Fn("vorbis_comment_query", 3); var count = v.Fn("vorbis_comment_query_count", 2);
        Assert.Equal(Ref("query-title", "Zeta Reticuli"), vorbis.Memory.ReadAnsiString(query.Call(v.Comment.Address, title.Address, 0)));   // tag match is case-insensitive
        Assert.Equal("Beta", vorbis.Memory.ReadAnsiString(query.Call(v.Comment.Address, artist.Address, 1)));
        Assert.Equal(0u, query.Call(v.Comment.Address, artist.Address, 2));
        Assert.Equal(Ref("query-count", 2u), count.Call(v.Comment.Address, artist.Address));
        Assert.Equal(0u, count.Call(v.Comment.Address, missing.Address));
        v.Fn("vorbis_comment_clear", 1).Call(v.Comment.Address);
    }

    private static GuestBuffer AllocText(GuestModule m, string text)
    {
        var b = m.Memory.Allocate(text.Length + 1); b.Write(0, [.. Encoding.ASCII.GetBytes(text), (byte)0]); return b;
    }

    // ---- vorbisFile: a real stream produced by ffmpeg/libvorbis, decoded and compared with ffmpeg's own decode ----

    private static string? Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); var err = p.StandardError.ReadToEnd();
            return p.WaitForExit(60000) && p.ExitCode == 0 ? null : "ffmpeg failed: " + err;
        }
        catch (System.ComponentModel.Win32Exception) { return "ffmpeg is not installed"; }
    }

    [Fact]
    public void VorbisFileDecodesARealStreamLikeFfmpegAcrossThreeModules()
    {
        if (Dir is null || !File.Exists(Path.Combine(Dir, "vorbisFile.dll"))) return;
        var work = Path.Combine(Path.GetTempPath(), "bcs-vorbis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var ogg = Path.Combine(work, "tone.ogg"); var pcm = Path.Combine(work, "tone.s16");
            if (Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=0.6:sample_rate=22050", "-ac", "2", "-c:a", "libvorbis", "-q:a", "3", ogg) is not null) return;   // no encoder here: nothing to test against
            if (Ffmpeg("-i", ogg, "-f", "s16le", "-acodec", "pcm_s16le", pcm) is not null) return;
            var stream = File.ReadAllBytes(ogg); var reference = File.ReadAllBytes(pcm);

            // The stream is served by host callbacks. vorbisFile.dll has no file imports of its own that we could use, so the four function pointers
            // handed to ov_open_callbacks are the guest-callable stubs of four kernel32 imports that this code path never calls (CRT file layer only);
            // those imports are bound to the callbacks below instead of the Win32 component.
            long position = 0;
            var imports = new DictionaryGuestImportResolver()
                .Add("KERNEL32.dll", "ReadFile", GuestImportBuilder.CdeclCall(c =>        // size_t read(void* ptr, size_t size, size_t nmemb, void* ds)
                {
                    var size = c.Arg(1); var n = c.Arg(2);
                    var take = (int)Math.Min((long)size * n, stream.Length - position);
                    if (take > 0) c.Memory.Write(c.Arg(0), stream.AsSpan((int)position, take));
                    position += take;
                    return size == 0 ? 0 : (uint)(take / size);
                }))
                .Add("KERNEL32.dll", "SetFilePointer", GuestImportBuilder.CdeclCall(c =>  // int seek(void* ds, ogg_int64_t offset, int whence)
                {
                    var offset = (long)((ulong)c.Arg(2) << 32 | c.Arg(1));
                    position = c.Arg(3) switch { 0 => offset, 1 => position + offset, 2 => stream.Length + offset, _ => -1 };
                    return position < 0 || position > stream.Length ? 0xFFFFFFFFu : 0u;
                }))
                .Add("KERNEL32.dll", "CloseHandle", GuestImportBuilder.CdeclCall(_ => 0))
                .Add("KERNEL32.dll", "FlushFileBuffers", GuestImportBuilder.CdeclCall(_ => (uint)position));   // long tell(void* ds)

            using var l = Open(["ogg", "vorbis", "vorbisFile"], name => name == "vorbisFile" ? imports : null)!;
            var vf = l["vorbisFile"];
            uint Stub(string n) => vf.GetImportStubAddress("KERNEL32.dll", n)!.Value;
            GuestFunction Fn(string n, int a) => vf.Bind(n, GuestCallingConvention.Cdecl, a);

            using var file = AllocZero(vf, 8192);    // OggVorbis_File (well above its real size)
            Assert.Equal(0u, Fn("ov_open_callbacks", 8).Call(0xF11E, file.Address, 0, 0, Stub("ReadFile"), Stub("SetFilePointer"), Stub("CloseHandle"), Stub("FlushFileBuffers")));

            var info = Fn("ov_info", 2).Call(file.Address, 0xFFFFFFFF);
            Assert.Equal(Ref("vf-channels", 2u), vf.Memory.ReadUInt32(info + 4));
            Assert.Equal(Ref("vf-rate", 22050u), vf.Memory.ReadUInt32(info + 8));
            Assert.Equal(Ref("vf-total", (ulong)Math.Round(0.6 * 22050)), Fn("ov_pcm_total", 2).CallUInt64(file.Address, 0xFFFFFFFF));
            Assert.Equal(Ref("vf-seconds", 0.6), Fn("ov_time_total", 2).CallDouble(file.Address, 0xFFFFFFFF), 6);

            short[] Decode(int samples)
            {
                var read = new List<short>(); using var buf = vf.Memory.Allocate(4096); using var bitstream = AllocZero(vf, 4);
                var tmp = new byte[4096];
                while (read.Count < samples * 2)
                {
                    var n = (int)Fn("ov_read", 7).Call(file.Address, buf.Address, 4096, 0, 2, 1, bitstream.Address);
                    Assert.True(n >= 0, $"ov_read failed with {n}");
                    if (n == 0) break;
                    buf.Read(0, tmp.AsSpan(0, n));
                    for (var i = 0; i < n; i += 2) read.Add(BitConverter.ToInt16(tmp, i));
                }
                return [.. read];
            }
            short[] Ref16(int from, int count) => Enumerable.Range(from * 2, count * 2).Select(i => BitConverter.ToInt16(reference, i * 2)).ToArray();
            int MaxDiff(short[] a, short[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();

            const int total = 13230;    // 0.6 s x 22050 Hz: the length we asked for; ffmpeg's own decode also returns the encoder's trailing padding, which vorbisFile trims by granule position
            Assert.True(reference.Length / 4 >= total);
            var all = Decode(total);
            Assert.Equal(total * 2, all.Length);
            var expected = Ref16(0, total);
            Assert.True(MaxDiff(all, expected) <= (Mutated("pcm") ? -1 : 2), $"max sample difference to ffmpeg's decode is {MaxDiff(all, expected)}");
            Assert.Equal(0u, Fn("ov_read", 7).Call(file.Address, AllocZero(vf, 16).Address, 16, 0, 2, 1, AllocZero(vf, 4).Address));   // EOF

            // seek back: ov_pcm_seek(vf, ogg_int64_t) exercises the seek/tell callbacks and ogg page resync across modules
            Assert.Equal(0u, Fn("ov_pcm_seek", 3).Call(file.Address, 5000, 0));
            Assert.Equal(Ref("vf-tell", 5000UL), Fn("ov_pcm_tell", 1).CallUInt64(file.Address));
            var tail = Decode(2000);
            Assert.True(MaxDiff(tail[..4000], Ref16(5000, 2000)) <= 2, "samples after the seek differ from ffmpeg's decode");
            Assert.True(l.Set.CrossModuleCalls.Where(c => c.Key.StartsWith("vorbisFile->ogg!")).Sum(c => c.Value) > 20);
            Assert.True(l.Set.CrossModuleCalls.Where(c => c.Key.StartsWith("vorbisFile->vorbis!")).Sum(c => c.Value) > 20);
            Assert.Equal(0u, Fn("ov_clear", 1).Call(file.Address));
        }
        finally { Directory.Delete(work, true); }
    }
}
