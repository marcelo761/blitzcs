using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// Winsock bookkeeping and byte-order helpers (WSOCK32 and WS2_32, by name and by ordinal). Ordinals checked against Windows-compatible wsock32 and ws2_32 export tables (Wine's, which keep the
/// Windows ordinals; both DLLs use the same numbers for these functions): 8 htonl, 9 htons, 14 ntohl, 15 ntohs, 111 WSAGetLastError, 112 WSASetLastError, 115 WSAStartup, 116 WSACleanup, 151 __WSAFDIsSet.
/// WSAStartup negotiates down to 2.2 (1.x versions are granted as requested up to 1.1), counts references per process and fills WSADATA (iMaxSockets/iMaxUdpDg 0, ignored since 2.0);
/// a zero major version fails with WSAVERNOTSUPPORTED (10092) and a major above 2 with the same code while still reporting wHighVersion 2.2. WSACleanup without a prior WSAStartup
/// fails with WSANOTINITIALISED (10093). WSAGetLastError is GetLastError (the same per-thread value on Windows). __WSAFDIsSet scans a real fd_set. SOCKETS THEMSELVES (socket, connect,
/// send, recv, select, ioctlsocket, closesocket, WSAAsyncGetHostByName ...) are NOT provided: the guest faults naming them.
/// </summary>
public sealed class WinsockCoreService : Win32Service
{
    public override string Name => "Winsock.Core";
    private const uint VerNotSupported = 10092, NotInitialised = 10093;
    private sealed class Ref { public int Count; }
    private static readonly ConditionalWeakTable<Win32Context, Ref> Refs = new();

    protected override void Define()
    {
        void Both(string name, ushort ordinal, int argc, Win32Handler h)
        {
            StdOrdinal("WSOCK32.dll", name, ordinal, argc, h);
            StdOrdinal("WS2_32.dll", name, ordinal, argc, h);
        }
        Both("htonl", 8, 1, c => BinaryPrimitives.ReverseEndianness(c[0]));
        Both("htons", 9, 1, c => BinaryPrimitives.ReverseEndianness((ushort)c[0]));
        Both("ntohl", 14, 1, c => BinaryPrimitives.ReverseEndianness(c[0]));
        Both("ntohs", 15, 1, c => BinaryPrimitives.ReverseEndianness((ushort)c[0]));
        Both("WSAGetLastError", 111, 0, c => c.Ctx.LastError);
        Both("WSASetLastError", 112, 1, c => { c.Ctx.LastError = c[0]; return 0; });
        Both("WSAStartup", 115, 2, c =>
        {
            var major = c[0] & 0xFF; var minor = c[0] >> 8 & 0xFF;
            var high = (ushort)0x0202;
            if (major == 0 || major > 2)
            {
                if (c[1] != 0) c.Mem.WriteUInt16(c[1] + 2, high);
                return VerNotSupported;
            }
            ushort granted = major == 1 ? (ushort)(minor >= 1 ? 0x0101 : 0x0001) : (ushort)(minor >= 2 ? 0x0202 : 0x0002 | minor << 8);
            var data = new byte[400];
            BinaryPrimitives.WriteUInt16LittleEndian(data, granted);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), high);
            "WinSock 2.0"u8.CopyTo(data.AsSpan(4));
            "Running"u8.CopyTo(data.AsSpan(261));
            c.Mem.Write(c[1], data);
            Interlocked.Increment(ref Refs.GetValue(c.Ctx, _ => new Ref()).Count);
            return 0;
        });
        Both("WSACleanup", 116, 0, c =>
        {
            var r = Refs.GetValue(c.Ctx, _ => new Ref());
            if (Interlocked.Decrement(ref r.Count) < 0) { Interlocked.Increment(ref r.Count); c.Ctx.LastError = NotInitialised; return 0xFFFFFFFF; }
            return 0;
        });
        Both("__WSAFDIsSet", 151, 2, c =>
        {
            var count = c.Mem.ReadUInt32(c[1]);
            for (uint i = 0; i < Math.Min(count, 64u); i++) if (c.Mem.ReadUInt32(c[1] + 4 + 4 * i) == c[0]) return 1;
            return 0;
        });
    }
}
