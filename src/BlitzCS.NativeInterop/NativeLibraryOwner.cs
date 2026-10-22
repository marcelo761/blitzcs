using System.Reflection;
using System.Runtime.InteropServices;

namespace BlitzCS.NativeInterop;

/// <summary>Owns one loaded native library and coordinates calls with explicit unloading.</summary>
public sealed class NativeLibraryOwner : IDisposable
{
    private readonly ReaderWriterLockSlim lifetime = new(LockRecursionPolicy.SupportsRecursion);
    private readonly ThreadLocal<int> invocationDepth = new(() => 0);
    private nint handle;
    private int liveResources;

    private NativeLibraryOwner(string path, nint handle)
    {
        Path = path;
        this.handle = handle;
    }

    public string Path { get; }

    /// <summary>Preflights the binary format, operating system, and process architecture, then loads it.</summary>
    public static NativeLibraryOwner Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new NativeLibraryLoadException(fullPath, $"Native library file does not exist: '{fullPath}'.");

        try
        {
            NativeBinaryPreflight.Validate(fullPath);
            return new(fullPath, NativeLibrary.Load(fullPath));
        }
        catch (NativeLibraryLoadException) { throw; }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            throw new NativeLibraryLoadException(fullPath, $"Could not load native library '{fullPath}': {error.Message}", error);
        }
    }

    /// <summary>Resolves an explicitly declared Cdecl delegate type for one named export.</summary>
    /// <remarks>The native ABI cannot be inferred from an export. The delegate signature must match a trusted native header.</remarks>
    public NativeFunction<TDelegate> Bind<TDelegate>(string exportName) where TDelegate : Delegate
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        var convention = typeof(TDelegate).GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention;
        if (convention != CallingConvention.Cdecl)
            throw new ArgumentException($"Native delegate '{typeof(TDelegate).Name}' must declare CallingConvention.Cdecl.", nameof(TDelegate));

        EnterCall();
        try
        {
            if (!NativeLibrary.TryGetExport(handle, exportName, out var address))
                throw new NativeExportException(Path, exportName);
            try { return new(this, Marshal.GetDelegateForFunctionPointer<TDelegate>(address)); }
            catch (Exception error) when (error is ArgumentException or MarshalDirectiveException)
            {
                throw new NativeExportException(Path, exportName, $"Export '{exportName}' in '{Path}' could not be bound as {typeof(TDelegate).Name}: {error.Message}", error);
            }
        }
        finally { ExitCall(); }
    }

    internal TResult Invoke<TDelegate, TResult>(TDelegate function, Func<TDelegate, TResult> call) where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(call);
        EnterCall();
        try { return call(function); }
        finally { ExitCall(); }
    }

    internal void Invoke<TDelegate>(TDelegate function, Action<TDelegate> call) where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(call);
        EnterCall();
        try { call(function); }
        finally { ExitCall(); }
    }

    internal void WithLifetime(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterCall();
        try { action(); }
        finally { ExitCall(); }
    }

    internal void RetainResource()
    {
        EnterCall();
        try { Interlocked.Increment(ref liveResources); }
        finally { ExitCall(); }
    }

    internal void ReleaseResource()
    {
        EnterCall();
        try
        {
            if (Interlocked.Decrement(ref liveResources) < 0)
            {
                Interlocked.Increment(ref liveResources);
                throw new InvalidOperationException("Native resource ownership is unbalanced.");
            }
        }
        finally { ExitCall(); }
    }

    public void Dispose()
    {
        if (invocationDepth.Value != 0)
            throw new InvalidOperationException("A native library cannot be disposed from inside one of its active invocations.");
        lifetime.EnterWriteLock();
        try
        {
            if (handle == 0) return;
            var resources = Volatile.Read(ref liveResources);
            if (resources != 0)
                throw new InvalidOperationException($"Cannot unload native library '{Path}' while {resources} native resource(s) still need its matching free function.");
            NativeLibrary.Free(handle);
            handle = 0;
        }
        finally { lifetime.ExitWriteLock(); }
    }

    private void EnterCall()
    {
        lifetime.EnterReadLock();
        if (handle == 0)
        {
            lifetime.ExitReadLock();
            throw new ObjectDisposedException(nameof(NativeLibraryOwner), $"Native library '{Path}' has been disposed.");
        }
        invocationDepth.Value++;
    }

    private void ExitCall()
    {
        invocationDepth.Value--;
        lifetime.ExitReadLock();
    }
}

/// <summary>A typed export whose calls are protected against concurrent library unloading.</summary>
/// <remarks>The callback is a trusted managed binding boundary. It must not retain the delegate or pass it to BASIC. The declared ABI must match the native header; export resolution cannot inspect a native function's signature.</remarks>
public sealed class NativeFunction<TDelegate> where TDelegate : Delegate
{
    private readonly NativeLibraryOwner owner;
    private readonly TDelegate function;

    internal NativeFunction(NativeLibraryOwner owner, TDelegate function)
    {
        this.owner = owner;
        this.function = function;
    }

    internal NativeLibraryOwner Owner => owner;

    public TResult Invoke<TResult>(Func<TDelegate, TResult> call) => owner.Invoke(function, call);
    public void Invoke(Action<TDelegate> call) => owner.Invoke(function, call);
}

public sealed class NativeLibraryLoadException : Exception
{
    public NativeLibraryLoadException(string path, string message, Exception? innerException = null) : base(message, innerException) => Path = path;
    public string Path { get; }
}

public sealed class NativeExportException : Exception
{
    public NativeExportException(string path, string exportName) : this(path, exportName, $"Native export '{exportName}' was not found in '{path}'.", null) { }
    public NativeExportException(string path, string exportName, string message, Exception? innerException) : base(message, innerException)
    {
        Path = path;
        ExportName = exportName;
    }
    public string Path { get; }
    public string ExportName { get; }
}

/// <summary>Owns a native-allocated UTF-8 byte resource and releases it with the library's matching free export.</summary>
public sealed class NativeUtf8Resource : IDisposable
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private readonly NativeLibraryOwner owner;
    private readonly NativeFunction<FreeResource> free;
    private nint pointer;
    private readonly int length;

    private NativeUtf8Resource(NativeLibraryOwner owner, NativeFunction<FreeResource> free, nint pointer, nuint length)
    {
        this.owner = owner;
        this.free = free;
        this.pointer = pointer;
        if (length > int.MaxValue)
        {
            free.Invoke(call => call(pointer));
            throw new ArgumentOutOfRangeException(nameof(length), "Native UTF-8 resource is too large to represent as a managed string.");
        }
        this.length = (int)length;
    }

    /// <summary>Adopts a resource returned by a typed native allocation export.</summary>
    /// <remarks>Call this inside the allocator's <see cref="NativeFunction{TDelegate}.Invoke{TResult}(Func{TDelegate, TResult})"/> callback so the library cannot unload between allocation and ownership transfer.</remarks>
    public static NativeUtf8Resource Own(NativeLibraryOwner owner, NativeFunction<FreeResource> free, nint pointer, nuint length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(free);
        if (!ReferenceEquals(free.Owner, owner))
            throw new ArgumentException("The resource free function must come from the same native library owner.", nameof(free));
        if (pointer == 0) throw new OutOfMemoryException("The native UTF-8 allocation returned a null pointer.");
        owner.RetainResource();
        try { return new(owner, free, pointer, length); }
        catch
        {
            owner.ReleaseResource();
            throw;
        }
    }

    public string Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(pointer == 0, this);
            var bytes = new byte[length];
            owner.WithLifetime(() => Marshal.Copy(pointer, bytes, 0, bytes.Length));
            return StrictUtf8.GetString(bytes);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (pointer == 0) return;
            var owned = pointer;
            free.Invoke(call => call(owned));
            pointer = 0;
            owner.ReleaseResource();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FreeResource(nint pointer);
}
