using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// The genuine MSVC build of libogg, initialised through its own CRT start-up (DllMain) on the Win32 component, then exercised
/// with allocation-heavy code (bit packer, stream/page/sync round trips). Expected values come from an independent C# model of the
/// Ogg bit-packing and CRC definitions, not from the translator. Nothing is copied into the repository: set BLITZCS_REAL_PE32_DIR.
/// </summary>
[Collection("GuestRuntime")]
public sealed class RealOggTests
{
    private static readonly uint[] CrcTable = BuildTable();
    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { var r = i << 24; for (var k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1; t[i] = r; }
        return t;
    }
    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0;
        foreach (var x in a) c = (c << 8) ^ CrcTable[((c >> 24) & 0xFF) ^ x];
        foreach (var x in b) c = (c << 8) ^ CrcTable[((c >> 24) & 0xFF) ^ x];
        return c;
    }

    private static (PeImage Image, TranslationArtifact Artifact) Load()
    {
        var path = RealLibraries.PathOf("ogg.dll");   // the callers are [RealLibraryFact]s: skipped at discovery when the file is absent
        var original = PeReader.Read(File.ReadAllBytes(path));
        var (image, loadBase) = HostBase.Fit(original);   // Windows: rebased into the host's image window
        return (image, X64Translator.Translate(original, new BinaryCompilerOptions(OptimizationProfile.Release, LoadBase: loadBase), "real-ogg"));
    }

    private static GuestModule Open((PeImage Image, TranslationArtifact Artifact) lib, Win32ImportResolver resolver) =>
        GuestModule.Load(lib.Artifact, lib.Image, resolver, new GuestModuleOptions { RunEntryPoint = true, AllowUnboundImports = true });

    [RealLibraryFact("ogg.dll")]
    public void CrtStartupRunsAndBitPackerMatchesTheFormatDefinition()
    {
        var lib = Load();
        Assert.DoesNotContain(lib.Artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        using var resolver = new Win32ImportResolver();
        using var module = Open(lib, resolver); // DllMain(DLL_PROCESS_ATTACH) runs the real CRT initialisation
        GuestFunction Fn(string n, int args) => module.Bind(n, GuestCallingConvention.Cdecl, args);
        var writeInit = Fn("oggpack_writeinit", 1); var write = Fn("oggpack_write", 3); var bytes = Fn("oggpack_bytes", 1);
        var getBuffer = Fn("oggpack_get_buffer", 1); var writeClear = Fn("oggpack_writeclear", 1);
        var readInit = Fn("oggpack_readinit", 3); var read = Fn("oggpack_read", 2);

        var seed = 0x13579BDFu;
        uint Next() { seed = seed * 1664525u + 1013904223u; return seed >> 7; }
        for (var round = 0; round < 40; round++)
        {
            using var buf = module.Memory.Allocate(32); buf.Write(0, new byte[32]);
            writeInit.Call(buf.Address);
            var entries = new List<(int Bits, uint Value)>();
            var model = new byte[4096]; long bitPos = 0;
            var count = 1 + (int)(Next() % 120);
            for (var i = 0; i < count; i++)
            {
                var bits = 1 + (int)(Next() % 32);
                var value = bits == 32 ? Next() * 2654435761u : Next() & ((1u << bits) - 1);
                entries.Add((bits, value));
                write.Call(buf.Address, value, (uint)bits);
                for (var b = 0; b < bits; b++, bitPos++) if (((value >> b) & 1) != 0) model[bitPos >> 3] |= (byte)(1 << (int)(bitPos & 7));
            }
            var expectedBytes = (int)((bitPos + 7) >> 3);
            Assert.Equal((uint)expectedBytes, bytes.Call(buf.Address));
            var ptr = getBuffer.Call(buf.Address);
            var got = new byte[expectedBytes]; module.Memory.Read(ptr, got);
            Assert.Equal(model[..expectedBytes], got);

            using var rbuf = module.Memory.Allocate(32); rbuf.Write(0, new byte[32]);
            readInit.Call(rbuf.Address, ptr, (uint)expectedBytes);
            foreach (var (bits, value) in entries) Assert.Equal(value, read.Call(rbuf.Address, (uint)bits));
            writeClear.Call(buf.Address);
        }
    }

    [RealLibraryFact("ogg.dll")]
    public void StreamPageSyncRoundTripThroughTheLibraryMatchesIndependentCrc()
    {
        var lib = Load();
        using var resolver = new Win32ImportResolver();
        using var module = Open(lib, resolver);
        GuestFunction Fn(string n, int args) => module.Bind(n, GuestCallingConvention.Cdecl, args);
        var streamInit = Fn("ogg_stream_init", 2); var packetIn = Fn("ogg_stream_packetin", 2); var flush = Fn("ogg_stream_flush", 2);
        var syncInit = Fn("ogg_sync_init", 1); var syncBuffer = Fn("ogg_sync_buffer", 2); var syncWrote = Fn("ogg_sync_wrote", 2);
        var pageOut = Fn("ogg_sync_pageout", 2); var pageIn = Fn("ogg_stream_pagein", 2); var packetOut = Fn("ogg_stream_packetout", 2);
        var streamClear = Fn("ogg_stream_clear", 1); var syncClear = Fn("ogg_sync_clear", 1);

        using var os = module.Memory.Allocate(1024); os.Write(0, new byte[1024]);
        using var os2 = module.Memory.Allocate(1024); os2.Write(0, new byte[1024]);
        using var oy = module.Memory.Allocate(128); oy.Write(0, new byte[128]);
        using var og = module.Memory.Allocate(16); using var og2 = module.Memory.Allocate(16);
        using var op = module.Memory.Allocate(32); using var op2 = module.Memory.Allocate(32);
        Assert.Equal(0u, streamInit.Call(os.Address, 0x1234ABCD));
        Assert.Equal(0u, streamInit.Call(os2.Address, 0x1234ABCD));
        syncInit.Call(oy.Address);

        var seed = 0xC0FFEEu;
        uint Next() { seed = seed * 1664525u + 1013904223u; return seed >> 8; }
        for (var round = 0; round < 25; round++)
        {
            var payload = new byte[1 + (int)(Next() % 900)];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)Next();
            using var data = module.Memory.Allocate(payload.Length); data.Write(0, payload);
            var packet = new byte[32];
            BitConverter.GetBytes(data.Address).CopyTo(packet, 0); BitConverter.GetBytes(payload.Length).CopyTo(packet, 4);
            BitConverter.GetBytes(round == 0 ? 1 : 0).CopyTo(packet, 8); BitConverter.GetBytes(round == 24 ? 1 : 0).CopyTo(packet, 12);
            BitConverter.GetBytes((long)round * 100).CopyTo(packet, 16); BitConverter.GetBytes((long)round).CopyTo(packet, 24);
            op.Write(0, packet);
            Assert.Equal(0u, packetIn.Call(os.Address, op.Address));
            Assert.NotEqual(0u, flush.Call(os.Address, og.Address));

            var page = new byte[16]; og.Read(0, page);
            var header = new byte[BitConverter.ToInt32(page, 4)]; var body = new byte[BitConverter.ToInt32(page, 12)];
            module.Memory.Read(BitConverter.ToUInt32(page, 0), header); module.Memory.Read(BitConverter.ToUInt32(page, 8), body);
            Assert.Equal("OggS"u8.ToArray(), header[..4]);
            var stored = BitConverter.ToUInt32(header, 22);
            var zeroed = (byte[])header.Clone(); zeroed[22] = zeroed[23] = zeroed[24] = zeroed[25] = 0;
            Assert.Equal(Crc(zeroed, body), stored); // the library's page checksum equals the format's CRC

            // Feed the exact bytes to a second stream through the library's own sync layer.
            var wire = header.Concat(body).ToArray();
            var dst = syncBuffer.Call(oy.Address, (uint)wire.Length);
            module.Memory.Write(dst, wire);
            Assert.Equal(0u, syncWrote.Call(oy.Address, (uint)wire.Length));
            Assert.Equal(1u, pageOut.Call(oy.Address, og2.Address));
            Assert.Equal(0u, pageIn.Call(os2.Address, og2.Address));
            Assert.Equal(1u, packetOut.Call(os2.Address, op2.Address));
            var outPacket = new byte[32]; op2.Read(0, outPacket);
            Assert.Equal(payload.Length, BitConverter.ToInt32(outPacket, 4));
            var echoed = new byte[payload.Length]; module.Memory.Read(BitConverter.ToUInt32(outPacket, 0), echoed);
            Assert.Equal(payload, echoed);
            Assert.Equal((long)round * 100, BitConverter.ToInt64(outPacket, 16));
        }
        streamClear.Call(os.Address); streamClear.Call(os2.Address); syncClear.Call(oy.Address);
    }
}
