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
public sealed class GuestTrapException : GuestException
{
    public GuestTrapException(string message) : base(message) { }
}
