using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>How one export is called by importing modules. A cdecl export has no argument count (see <see cref="ModuleLinkOptions.CdeclWindowDwords"/>); a stdcall export must state it.</summary>
public sealed record ExportSignature(GuestCallingConvention Convention, int? ArgCount = null)
{
    public static ExportSignature Cdecl { get; } = new(GuestCallingConvention.Cdecl);
    public static ExportSignature Stdcall(int argCount) => new(GuestCallingConvention.Stdcall, argCount);
}

/// <summary>
/// Calling conventions of one module's exports, declared by whoever links the module (a PE export table carries none).
/// Resolution for an imported export: an explicit <see cref="Exports"/> entry (name, or "#ordinal"); else the convention the compiler read from an
/// unambiguous MSVC decoration (<c>_f</c>, <c>_f@12</c>); else <see cref="Default"/>. Nothing is guessed: with none of these the link fails and names the import.
/// </summary>
public sealed class ExportConventions
{
    /// <summary>Applied to undecorated exports without an explicit entry. Only <see cref="GuestCallingConvention.Cdecl"/> is accepted (stdcall needs a per-export argument count).</summary>
    public GuestCallingConvention? Default { get; init; }

    public IReadOnlyDictionary<string, ExportSignature> Exports { get; init; } = new Dictionary<string, ExportSignature>(StringComparer.Ordinal);

    /// <summary>Every undecorated export is cdecl (typical for C libraries built without a .def file such as libogg and libvorbis).</summary>
    public static ExportConventions AllCdecl { get; } = new() { Default = GuestCallingConvention.Cdecl };
}

public sealed class ModuleLinkOptions
{
    /// <summary>
    /// Number of 32-bit stack words copied from the caller's frame to a cdecl export of another module (default 16, maximum 256). Exports carry no
    /// argument count, so the bridge passes a bounded window starting at the first argument; the callee reads only the arguments it really has and
    /// ignores the rest (the words beyond the real arguments are caller locals/return addresses and are never written back). The window is shortened
    /// so it never reaches past the mapped end of the caller's stack. A cdecl callee with more arguments than the window sees garbage for the extra
    /// ones: raise the window or declare the export stdcall with an exact count.
    /// </summary>
    public int CdeclWindowDwords { get; init; } = 16;
}

/// <summary>Chains resolvers: the first one that provides the import wins. Compose host services (Win32 component) before sibling modules without either depending on the other.</summary>
public sealed class ChainedGuestImportResolver(params IGuestImportResolver[] resolvers) : IGuestImportResolver
{
    private readonly IGuestImportResolver[] resolvers = [.. resolvers];

    public GuestImport? Resolve(string dll, string? name, ushort? ordinal)
    {
        foreach (var r in resolvers)
            if (r.Resolve(dll, name, ordinal) is { } import) return import;
        return null;
    }
}

/// <summary>Builds the managed bridge that forwards an import of <c>other.dll!name</c> to the export in the other module's address space.</summary>
internal sealed class BridgeCounter(string label)
{
    public readonly string Label = label;
    public long Calls;
}

internal static class ModuleBridge
{
    internal static GuestImport Create(GuestModule target, TranslatedExport export, ExportSignature signature, int window, BridgeCounter counter)
    {
        var (handler, pop) = CreateHandler(target, export, signature, window, counter);
        return GuestImport.Create(handler, pop);
    }

    /// <summary>The forwarding handler and the bytes the guest stub must pop: shared by static module imports and by the stubs GetProcAddress hands out.</summary>
    internal static (GuestImportHandler Handler, uint Pop) CreateHandler(GuestModule target, TranslatedExport export, ExportSignature signature, int window, BridgeCounter counter)
    {
        var host = target.HostImageBase + (nint)export.HostOffset;
        var stdcall = signature.Convention == GuestCallingConvention.Stdcall;
        var words = stdcall ? signature.ArgCount!.Value : window;
        var pop = stdcall ? checked((uint)(4 * words)) : 0u;
        GuestImportHandler handler = call =>
        {
            Interlocked.Increment(ref counter.Calls);
            Span<uint> args = stackalloc uint[words];
            var n = Capture(call, args, exact: stdcall);
            var result = target.Invoke(host, args[..n], GuestModule.St0IfPresent);
            call.SetReturnEdx(result.Edx);
            if (result.Kind == GuestReturn.St0)
            {
                Span<byte> raw = stackalloc byte[10];
                BitConverter.TryWriteBytes(raw, result.St0Mantissa);
                BitConverter.TryWriteBytes(raw[8..], result.St0SignExponent);
                call.SetReturnSt0(raw);
            }
            return result.Eax;
        };
        return (handler, pop);
    }

    private static int Capture(GuestCall call, Span<uint> destination, bool exact)
    {
        var first = call.Esp + 4;
        var n = destination.Length;
        if (!exact)
            while (n > 0 && ((ulong)first + 4UL * (uint)n > uint.MaxValue || !call.Memory.IsMapped(first, 4u * (uint)n))) n--;
        if (n > 0) call.Memory.Read(first, MemoryMarshal.AsBytes(destination[..n]));
        return n;
    }
}
