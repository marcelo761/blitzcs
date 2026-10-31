namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Base type for every failure raised by the guest runtime.</summary>
public class GuestException : Exception
{
    public GuestException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The artifact, image or host environment cannot be loaded.</summary>
public class GuestLoadException : GuestException
{
    public GuestLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The image cannot be mapped at the fixed base the code was translated for (translated code embeds absolute addresses).</summary>
public sealed class GuestAddressSpaceException : GuestLoadException
{
    public GuestAddressSpaceException(string message) : base(message) { }
}

/// <summary>A PE feature the runtime refuses to emulate silently (for example a TLS directory).</summary>
public sealed class GuestUnsupportedException : GuestLoadException
{
    public GuestUnsupportedException(string message) : base(message) { }
}

/// <summary>DllMain(DLL_PROCESS_ATTACH) failed or could not be run.</summary>
public sealed class GuestInitializationException : GuestLoadException
{
    public GuestInitializationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>An import is unbound at load time, or a managed import faulted during a guest call.</summary>
public sealed class GuestImportException : GuestException
{
    public GuestImportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A managed access to guest memory fell outside the mapped (or writable) ranges.</summary>
public sealed class GuestMemoryException : GuestException
{
    public GuestMemoryException(uint address, long length, bool write)
        : base($"Guest {(write ? "write" : "read")} of {length} byte(s) at 0x{address:X8} is outside mapped{(write ? " writable" : "")} guest memory.")
    {
        Address = address;
    }

    public uint Address { get; }
}

/// <summary>The guest stack budget would be exceeded, or a stack canary above a call frame was overwritten.</summary>
public sealed class GuestStackException : GuestException
{
    public GuestStackException(string message) : base(message) { }
}

/// <summary>An export could not be found or bound with the requested convention.</summary>
public sealed class GuestExportException : GuestException
{
    public GuestExportException(string message) : base(message) { }
}

/// <summary>Translated code hit an explicit trap (untranslated indirect target or an instruction rejected at build time). Never a silent zero result.</summary>
public class GuestTrapException : GuestException
{
    public GuestTrapException(string message) : base(message) { }
}

/// <summary>
/// The guest wrote into bytes of code that was translated at run time (docs/binary-dynamic-code.md): the translation was made from the bytes as they were, so running on
/// would execute stale code. Raised for a guest store (trap) and for a managed write through <see cref="GuestMemory"/>. Writes to data that shares the pages are allowed.
/// </summary>
public sealed class GuestSelfModifyingCodeException : GuestTrapException
{
    public GuestSelfModifyingCodeException(uint address, uint regionStart, uint regionEnd, string snapshotSha256)
        : base($"Self-modifying code: the guest wrote to 0x{address:X8}, an instruction byte of code translated at run time from the executable region 0x{regionStart:X8}-0x{regionEnd:X8} (snapshot SHA-256 {snapshotSha256[..Math.Min(16, snapshotSha256.Length)]}). The translation is bound to the bytes it was made from, so the write is refused instead of running stale code.")
    {
        Address = address; RegionStart = regionStart; RegionEnd = regionEnd;
    }

    public uint Address { get; }
    public uint RegionStart { get; }
    public uint RegionEnd { get; }
}

/// <summary>The guest executed <c>int 29h</c> (the MSVC <c>__fastfail</c> intrinsic: stack cookie failure, invalid argument, CRT abort ...). Distinct from <see cref="GuestTrapException"/>: the guest ran correctly and asked the process to die.</summary>
public sealed class GuestFastFailException : GuestException
{
    public GuestFastFailException(uint code, uint address)
        : base($"The guest requested a fast fail (int 29h) at 0x{address:X8}: code {code} ({CodeName(code)}).")
    {
        Code = code; Address = address;
    }

    /// <summary>ECX at the <c>int 29h</c>: one of the FAST_FAIL_* values.</summary>
    public uint Code { get; }

    /// <summary>Guest address of the <c>int 29h</c> instruction.</summary>
    public uint Address { get; }

    /// <summary>Name of a well-known FAST_FAIL_* code (winnt.h); "FAST_FAIL_<n>" otherwise.</summary>
    public static string CodeName(uint code) => code switch
    {
        0 => "FAST_FAIL_LEGACY_GS_VIOLATION", 1 => "FAST_FAIL_VTGUARD_CHECK_FAILURE", 2 => "FAST_FAIL_STACK_COOKIE_CHECK_FAILURE",
        3 => "FAST_FAIL_CORRUPT_LIST_ENTRY", 4 => "FAST_FAIL_INCORRECT_STACK", 5 => "FAST_FAIL_INVALID_ARG", 6 => "FAST_FAIL_GS_COOKIE_INIT",
        7 => "FAST_FAIL_FATAL_APP_EXIT", 8 => "FAST_FAIL_RANGE_CHECK_FAILURE", 9 => "FAST_FAIL_UNSAFE_REGISTRY_ACCESS",
        10 => "FAST_FAIL_GUARD_ICALL_CHECK_FAILURE", 11 => "FAST_FAIL_GUARD_WRITE_CHECK_FAILURE", 12 => "FAST_FAIL_INVALID_FIBER_SWITCH",
        13 => "FAST_FAIL_INVALID_SET_OF_CONTEXT", 14 => "FAST_FAIL_INVALID_REFERENCE_COUNT", 18 => "FAST_FAIL_INVALID_JUMP_BUFFER",
        19 => "FAST_FAIL_MRDATA_MODIFIED", 20 => "FAST_FAIL_CERTIFICATION_FAILURE", 21 => "FAST_FAIL_INVALID_EXCEPTION_CHAIN",
        22 => "FAST_FAIL_CRYPTO_LIBRARY", 23 => "FAST_FAIL_INVALID_CALL_IN_DLL_CALLOUT", 24 => "FAST_FAIL_INVALID_IMAGE_BASE",
        25 => "FAST_FAIL_DLOAD_PROTECTION_FAILURE", 26 => "FAST_FAIL_UNSAFE_EXTENSION_CALL", 27 => "FAST_FAIL_DEPRECATED_SERVICE_INVOKED",
        28 => "FAST_FAIL_INVALID_BUFFER_ACCESS", 29 => "FAST_FAIL_INVALID_BALANCED_TREE", 30 => "FAST_FAIL_INVALID_NEXT_THREAD",
        31 => "FAST_FAIL_GUARD_ICALL_CHECK_SUPPRESSED", 32 => "FAST_FAIL_APCS_DISABLED", 33 => "FAST_FAIL_INVALID_IDLE_STATE",
        34 => "FAST_FAIL_MRDATA_PROTECTION_FAILURE", 35 => "FAST_FAIL_UNEXPECTED_HEAP_EXCEPTION",
        _ => "FAST_FAIL_" + code,
    };
}

/// <summary>The guest violated (or the call site mis-declared) the calling convention in a way the runtime can detect: x87 stack imbalance, or a requested return register that was not produced.</summary>
public sealed class GuestAbiException : GuestException
{
    public GuestAbiException(string message) : base(message) { }
}
