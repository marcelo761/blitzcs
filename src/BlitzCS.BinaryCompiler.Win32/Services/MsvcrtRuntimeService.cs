using System.Runtime.CompilerServices;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// MSVCRT start-up, math, stdio and locale-data exports used by real DLLs (cdecl; "C" locale only, no setlocale).
/// _initterm/_initterm_e call each non-NULL function pointer of [first,last) in order through <see cref="Win32Call.CallGuest"/>; _initterm_e stops at the first non-zero result and returns it.
/// _ftol/_CIpow take their arguments in x87 registers (read with <see cref="Win32Call.PopSt0Double"/>); _ftol truncates toward zero into EDX:EAX and gives the x87 "integer indefinite"
/// 0x8000000000000000 for NaN/overflow. sin/cos/atan/log/pow come from the host libm (IEEE double; the msvcrt x87/SSE2 kernels may differ in the last ulp); sqrt, fabs, floor, fmod, ldexp, frexp are exact.
/// errno (per-thread, <c>_errno()</c>) is set to EDOM(33)/ERANGE(34) for sqrt/log/fmod/pow domain errors; no other function here touches it except fopen (ENOENT 2, EINVAL 22) and wcstombs (EILSEQ 42).
/// rand/srand are the documented linear congruential generator (seed*214013+2531011, result bits 16..30, per-thread seed starting at 1). clock() is milliseconds since the process started
/// (CLOCKS_PER_SEC = 1000). qsort sorts with a stable merge sort calling the guest comparer on the original element addresses (the real qsort is not stable, so the order of EQUAL elements may differ).
/// Data exports: __mb_cur_max = 1, _adjust_fdiv = 0 (no FDIV flaw), _pctype = pointer to the 257-entry "C" locale character-class table (index -1..255, _UPPER 1 _LOWER 2 _DIGIT 4 _SPACE 8 _PUNCT 0x10 _CONTROL 0x20 _BLANK 0x40 _HEX 0x80 _ALPHA 0x100).
/// stdio: fopen supports binary modes only (r, w, a with optional + and b required); text-mode translation is not implemented, so a text mode returns NULL with errno EINVAL. Files live in the sandbox
/// (Win32Options.SandboxRoot). FILE objects are 32-byte guest records; fread/fseek/ftell/fclose on a pointer that is not an open FILE raise Win32UsageException (the CRT would crash).
/// wcstombs converts code units below 256 one-to-one and fails (-1, EILSEQ) on anything above, as the "C" locale does. operator new/delete use the CRT heap (new returns NULL on exhaustion).
/// </summary>
public sealed class MsvcrtRuntimeService : Win32Service
{
    public override string Name => "Msvcrt.Runtime";
    private const string M = "MSVCRT.dll";
    private const uint EDOM = 33, ERANGE = 34, ENOENT = 2, EINVAL = 22, EILSEQ = 42;

    private sealed class State
    {
        public readonly ThreadLocal<uint> Seed = new(() => 1);
        public readonly Dictionary<uint, (FileStream Stream, IWin32Allocation Record)> Files = [];
        public readonly object Gate = new();
    }

    private static readonly ConditionalWeakTable<Win32Context, State> States = new();
    private static State Of(Win32Call c) => States.GetValue(c.Ctx, _ => new State());

    private static uint ErrnoPtr(Win32Call c) => c.Ctx.Process.ThreadBuffer(c.Mem, "errno", 4);
    private static void SetErrno(Win32Call c, uint v) => c.Mem.WriteUInt32(ErrnoPtr(c), v);

    private static ushort[] CTable()
    {
        var t = new ushort[257]; // t[i + 1] describes character i; t[0] is EOF (-1)
        for (var ch = 0; ch < 128; ch++)
        {
            ushort v = 0;
            if (ch < 32 || ch == 127) v |= 0x20;
            if (ch is >= 9 and <= 13) v |= 0x8;
            if (ch == 32) v |= 0x48;
            if (ch is >= '0' and <= '9') v |= 0x84;
            if (ch is >= 'A' and <= 'Z') v |= 0x101;
            if (ch is >= 'a' and <= 'z') v |= 0x102;
            if (ch is >= 'A' and <= 'F' or >= 'a' and <= 'f') v |= 0x80;
            if (ch is > 32 and < 127 && !char.IsAsciiLetterOrDigit((char)ch)) v |= 0x10;
            t[ch + 1] = v;
        }
        return t;
    }

    protected override void Define()
    {
        Data(M, "__mb_cur_max", (ctx, mem) => { var a = mem.Allocate(4); mem.WriteUInt32(a.Address, 1); return a.Address; });
        Data(M, "_adjust_fdiv", (ctx, mem) => mem.Allocate(4).Address);
        Data(M, "_pctype", (ctx, mem) =>
        {
            var table = mem.Allocate(257 * 2 + 2);
            var bytes = new byte[257 * 2];
            var t = CTable();
            for (var i = 0; i < t.Length; i++) { bytes[2 * i] = (byte)t[i]; bytes[2 * i + 1] = (byte)(t[i] >> 8); }
            mem.Write(table.Address, bytes);
            var variable = mem.Allocate(4);
            mem.WriteUInt32(variable.Address, table.Address + 2); // _pctype points at the entry of character 0
            return variable.Address;
        });
        Cdecl(M, "_isctype", 2, c =>
        {
            var ch = (int)c[0];
            if (ch < -1 || ch > 255) return 0;
            return (uint)(CTable()[ch + 1] & (ushort)c[1]);
        });

        Cdecl(M, "_initterm", 2, c =>
        {
            for (var p = c[0]; p < c[1]; p += 4) { var fn = c.Mem.ReadUInt32(p); if (fn != 0) c.CallGuest(fn); }
            return 0;
        });
        Cdecl(M, "_initterm_e", 2, c =>
        {
            for (var p = c[0]; p < c[1]; p += 4)
            {
                var fn = c.Mem.ReadUInt32(p);
                if (fn == 0) continue;
                var r = c.CallGuest(fn);
                if (r != 0) return r;
            }
            return 0;
        });
        Cdecl(M, "_errno", 0, ErrnoPtr);

        Cdecl(M, "_ftol", 0, c =>
        {
            var v = c.PopSt0Double();
            long r = double.IsNaN(v) || v >= 9.2233720368547758E18 || v < -9.2233720368547758E18 ? long.MinValue : (long)v;
            c.ReturnEdx((uint)((ulong)r >> 32));
            return (uint)r;
        });
        Cdecl(M, "_CIpow", 0, c =>
        {
            var y = c.PopSt0Double(); var x = c.PopSt0Double();
            c.ReturnSt0(Pow(c, x, y));
            return 0;
        });
        Cdecl(M, "sin", 2, c => { c.ReturnSt0(Math.Sin(c.Double(0))); return 0; });
        Cdecl(M, "cos", 2, c => { c.ReturnSt0(Math.Cos(c.Double(0))); return 0; });
        Cdecl(M, "atan", 2, c => { c.ReturnSt0(Math.Atan(c.Double(0))); return 0; });
        Cdecl(M, "fabs", 2, c => { c.ReturnSt0(Math.Abs(c.Double(0))); return 0; });
        Cdecl(M, "floor", 2, c => { c.ReturnSt0(Math.Floor(c.Double(0))); return 0; });
        Cdecl(M, "sqrt", 2, c => { var x = c.Double(0); if (x < 0) SetErrno(c, EDOM); c.ReturnSt0(Math.Sqrt(x)); return 0; });
        Cdecl(M, "log", 2, c =>
        {
            var x = c.Double(0);
            if (x < 0) SetErrno(c, EDOM); else if (x == 0) SetErrno(c, ERANGE);
            c.ReturnSt0(Math.Log(x)); return 0;
        });
        Cdecl(M, "pow", 4, c => { c.ReturnSt0(Pow(c, c.Double(0), c.Double(2))); return 0; });
        Cdecl(M, "fmod", 4, c =>
        {
            var x = c.Double(0); var y = c.Double(2);
            if (y == 0 && !double.IsNaN(x)) SetErrno(c, EDOM);
            c.ReturnSt0(x % y); return 0; // C# % on doubles is C fmod
        });
        Cdecl(M, "ldexp", 3, c => { c.ReturnSt0(Math.ScaleB(c.Double(0), (int)c[2])); return 0; });
        Cdecl(M, "frexp", 3, c =>
        {
            var x = c.Double(0);
            int exp;
            double m;
            if (x == 0 || !double.IsFinite(x)) { exp = 0; m = x; }
            else { exp = Math.ILogB(x) + 1; m = Math.ScaleB(x, -exp); }
            c.Mem.WriteUInt32(c[2], (uint)exp);
            c.ReturnSt0(m); return 0;
        });

        Cdecl(M, "clock", 0, _ => (uint)(System.Diagnostics.Stopwatch.GetElapsedTime(ProcessState.StartTicks).TotalMilliseconds));
        Cdecl(M, "srand", 1, c => { Of(c).Seed.Value = c[0]; return 0; });
        Cdecl(M, "rand", 0, c =>
        {
            var st = Of(c);
            st.Seed.Value = unchecked(st.Seed.Value * 214013u + 2531011u);
            return st.Seed.Value >> 16 & 0x7FFF;
        });

        Cdecl(M, "qsort", 4, c =>
        {
            uint basePtr = c[0], num = c[1], width = c[2], cmp = c[3];
            if (num < 2 || width == 0) return 0;
            var call = c.GuestCaller();
            var order = Enumerable.Range(0, (int)num).ToArray();
            var tmp = new int[order.Length];
            void Sort(int lo, int hi)
            {
                if (hi - lo < 2) return;
                var mid = (lo + hi) / 2;
                Sort(lo, mid); Sort(mid, hi);
                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi)
                    tmp[k++] = (int)call(cmp, [basePtr + (uint)order[j] * width, basePtr + (uint)order[i] * width]) < 0 ? order[j++] : order[i++];
                while (i < mid) tmp[k++] = order[i++];
                while (j < hi) tmp[k++] = order[j++];
                Array.Copy(tmp, lo, order, lo, hi - lo);
            }
            Sort(0, order.Length);
            var all = c.Mem.ReadBytes(basePtr, checked((int)(num * width)));
            for (var n = 0; n < order.Length; n++) c.Mem.Write(basePtr + (uint)n * width, all.AsSpan(order[n] * (int)width, (int)width));
            return 0;
        });

        Cdecl(M, "_vsnprintf", 4, c =>
        {
            var list = c[3]; var mem = c.Mem;
            CrtService.Format(c.Mem, i => mem.ReadUInt32(list + 4u * (uint)i), c[2], 0, out var bytes);
            var cap = c[1];
            if (bytes.Length < cap) { c.Mem.Write(c[0], [.. bytes, (byte)0]); return (uint)bytes.Length; }
            if (cap > 0) c.Mem.Write(c[0], bytes.AsSpan(0, (int)cap));
            return bytes.Length == cap ? (uint)bytes.Length : 0xFFFFFFFF;
        });

        Cdecl(M, "wcstombs", 3, c =>
        {
            var src = c.Mem.ReadWideString(c[1]);
            foreach (var ch in src) if (ch > 255) { SetErrno(c, EILSEQ); return 0xFFFFFFFF; }
            if (c[0] == 0) return (uint)src.Length;
            var n = (int)Math.Min((uint)src.Length, c[2]);
            var bytes = new byte[n + (n < c[2] ? 1 : 0)];
            for (var i = 0; i < n; i++) bytes[i] = (byte)src[i];
            c.Mem.Write(c[0], bytes);
            return (uint)n;
        });

        Cdecl(M, "??2@YAPAXI@Z", 1, c => c[0] > c.Ctx.Options.MaxAllocation ? 0 : c.Ctx.Heaps.Crt(c.Mem).Alloc(c[0], false));
        Cdecl(M, "??3@YAXPAX@Z", 1, c =>
        {
            if (c[0] != 0 && !c.Ctx.Heaps.Crt(c.Mem).Free(c[0])) throw new Win32UsageException($"operator delete(0x{c[0]:X8}): not a live block of the CRT heap.");
            return 0;
        });

        Cdecl(M, "fopen", 2, c =>
        {
            var mode = c.Mem.ReadAnsiString(c[1], 16);
            var plus = mode.Contains('+');
            var kind = mode.Length > 0 ? mode[0] : ' ';
            if (kind is not ('r' or 'w' or 'a') || !mode.Contains('b') || mode.Any(ch => ch is not ('r' or 'w' or 'a' or '+' or 'b'))) { SetErrno(c, EINVAL); return 0; }
            var (host, error) = c.Ctx.Files.Translate(c.Mem.ReadAnsiString(c[0], 4096));
            if (host is null) { SetErrno(c, error == Win32Error.AccessDenied ? 13u : ENOENT); return 0; }
            FileStream stream;
            try
            {
                stream = kind switch
                {
                    'r' => new FileStream(host, FileMode.Open, plus ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite),
                    'w' => new FileStream(host, FileMode.Create, plus ? FileAccess.ReadWrite : FileAccess.Write, FileShare.ReadWrite),
                    _ => new FileStream(host, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                SetErrno(c, e is FileNotFoundException or DirectoryNotFoundException ? ENOENT : 13u);
                return 0;
            }
            var record = c.Mem.Allocate(32);
            var st = Of(c);
            lock (st.Gate) st.Files[record.Address] = (stream, record);
            return record.Address;
        });
        Cdecl(M, "fclose", 1, c =>
        {
            var st = Of(c);
            (FileStream Stream, IWin32Allocation Record) f;
            lock (st.Gate) { if (!st.Files.Remove(c[0], out f)) throw new Win32UsageException($"fclose(0x{c[0]:X8}): not an open FILE."); }
            f.Stream.Dispose(); f.Record.Dispose();
            return 0;
        });
        Cdecl(M, "fread", 4, c =>
        {
            var f = Open(c, c[3], "fread");
            if (c[1] == 0 || c[2] == 0) return 0;
            var total = (long)c[1] * c[2];
            if (total > c.Ctx.Options.MaxAllocation) throw new Win32UsageException("fread: request exceeds the maximum allocation.");
            var buffer = new byte[total];
            var got = 0;
            while (got < total) { var n = f.Read(buffer, got, (int)(total - got)); if (n == 0) break; got += n; }
            c.Mem.Write(c[0], buffer.AsSpan(0, got));
            return (uint)(got / c[1]);
        });
        Cdecl(M, "fseek", 3, c =>
        {
            var f = Open(c, c[0], "fseek");
            if (c[2] > 2) { SetErrno(c, EINVAL); return 0xFFFFFFFF; }
            var target = (SeekOrigin)c[2] switch { SeekOrigin.Begin => (long)(int)c[1], SeekOrigin.Current => f.Position + (int)c[1], _ => f.Length + (int)c[1] };
            if (target < 0) { SetErrno(c, EINVAL); return 0xFFFFFFFF; }
            f.Position = target;
            return 0;
        });
        Cdecl(M, "ftell", 1, c => (uint)Open(c, c[0], "ftell").Position);
    }

    private static FileStream Open(Win32Call c, uint file, string api)
    {
        var st = Of(c);
        lock (st.Gate) return st.Files.TryGetValue(file, out var f) ? f.Stream : throw new Win32UsageException($"{api}(0x{file:X8}): not an open FILE.");
    }

    private static double Pow(Win32Call c, double x, double y)
    {
        var r = Math.Pow(x, y);
        if (double.IsNaN(r) && !double.IsNaN(x) && !double.IsNaN(y)) SetErrno(c, EDOM);
        return r;
    }
}
