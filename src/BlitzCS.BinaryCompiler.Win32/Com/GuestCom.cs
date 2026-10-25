using System.Runtime.CompilerServices;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32.Com;

/// <summary>HRESULT values used by the COM layer (winerror.h). They are identical on the 32-bit guest side and the 64-bit host side, so a host HRESULT is returned to the guest unchanged.</summary>
public static class ComHr
{
    public const uint Ok = 0, False = 1, NotImpl = 0x80004001, NoInterface = 0x80004002, Pointer = 0x80004003, Fail = 0x80004005,
        Unexpected = 0x8000FFFF, InvalidArg = 0x80070057, OutOfMemory = 0x8007000E;
    public static bool Succeeded(uint hr) => (int)hr >= 0;
}

public static class ComIids
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
}

/// <summary>A COM call from the guest: <c>this</c> is argument 0 of the stdcall frame, method arguments follow.</summary>
public readonly struct GuestComCall
{
    internal GuestComCall(GuestComHost host, GuestComObject obj, GuestComFace face, GuestCall raw) { Host = host; Object = obj; Face = face; Raw = raw; }
    public GuestComHost Host { get; }
    public GuestComObject Object { get; }
    public GuestComFace Face { get; }
    public GuestCall Raw { get; }
    public IWin32Memory Mem => Host.Memory;
    /// <summary>Method argument <paramref name="index"/> (0 = the first argument after <c>this</c>), as the 32-bit word the guest pushed.</summary>
    public uint Arg(int index) => Raw.Arg(index + 1);
    public Win32Context Ctx => Host.Context;
    /// <summary>Calls guest code (callbacks) on this thread, inside the handler.</summary>
    public uint CallGuest(uint address, params ReadOnlySpan<uint> args) => Raw.CallGuest(address, args);
}

public delegate uint GuestComHandler(GuestComCall call);

/// <summary>One slot of a COM interface: the managed handler and the exact number of stdcall arguments after <c>this</c>. A null handler answers E_NOTIMPL, which is a real COM answer only for a method that exists in the interface but is deliberately not implemented.</summary>
public sealed record GuestComMethod(string Name, int ArgCount, GuestComHandler? Handler)
{
    /// <summary>Why a null-handler method is not implemented (documentation that travels with the table).</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// The shape of a COM interface the guest sees: IID, the IUnknown methods (implemented by <see cref="GuestComHost"/>) and the interface's own methods in vtable order.
/// <see cref="AlsoAnswers"/> lists further IIDs a QueryInterface for which returns this same face (an interface that IS-A another with an identical layout prefix).
/// </summary>
public sealed class GuestComInterface
{
    private readonly List<GuestComMethod> methods = [];
    private readonly List<Guid> also = [];

    public GuestComInterface(string name, Guid iid) { Name = name; Iid = iid; }

    public string Name { get; }
    public Guid Iid { get; }
    public IReadOnlyList<GuestComMethod> Methods => methods;
    public IReadOnlyList<Guid> AlsoAnswers => also;

    public GuestComInterface Method(string name, int argCount, GuestComHandler handler) { methods.Add(new(name, argCount, handler ?? throw new ArgumentNullException(nameof(handler)))); return this; }
    public GuestComInterface NotImplemented(string name, int argCount, string reason) { methods.Add(new(name, argCount, null) { Reason = reason }); return this; }
    public GuestComInterface Answers(Guid iid) { also.Add(iid); return this; }
    public GuestComInterface Add(GuestComMethod method) { methods.Add(method); return this; }

    internal bool Matches(Guid iid) => iid == Iid || also.Contains(iid);
}

/// <summary>One COM interface pointer of a guest-visible object: an 8-byte guest block [vtable pointer][instance id].</summary>
public sealed class GuestComFace
{
    internal GuestComFace(GuestComObject owner, GuestComInterface iface, uint address) { Owner = owner; Interface = iface; Address = address; }
    public GuestComObject Owner { get; }
    public GuestComInterface Interface { get; }
    public uint Address { get; }
}

/// <summary>A COM object living in the guest: one shared reference count over one or more interface faces. State is whatever the host side needs (a native interface pointer, a buffer shadow ...).</summary>
public sealed class GuestComObject
{
    private int refs = 1;
    private int destroyed;
    private readonly List<GuestComFace> faces = [];
    internal Action<GuestComObject>? OnDestroy;

    internal GuestComObject(int id, string name, object? state) { Id = id; Name = name; State = state; }

    public int Id { get; }
    public string Name { get; }
    public object? State { get; }
    public int RefCount => Volatile.Read(ref refs);
    public bool IsAlive => Volatile.Read(ref destroyed) == 0;
    public IReadOnlyList<GuestComFace> Faces => faces;
    /// <summary>The first interface's pointer: what is handed to the guest.</summary>
    public uint Address => faces[0].Address;
    public uint FaceAddress(GuestComInterface iface) => faces.First(f => f.Interface == iface).Address;
    internal void AddFace(GuestComFace face) => faces.Add(face);
    internal int AddRefCore() { int current; do { current = Volatile.Read(ref refs); if (current <= 0) return 0; } while (Interlocked.CompareExchange(ref refs, current + 1, current) != current); return current + 1; }
    internal int ReleaseCore() => Interlocked.Decrement(ref refs);
    internal bool TryMarkDestroyed() => Interlocked.Exchange(ref destroyed, 1) == 0;
    public override string ToString() => $"{Name} #{Id}";
}

/// <summary>Raised when the guest breaks COM rules: calling through a destroyed object, releasing twice, passing a pointer that is no COM object of this module.</summary>
public sealed class GuestComException(string message) : Exception(message);

/// <summary>
/// Creates COM objects in guest low memory, backed by managed handlers.
///
/// Each interface gets one vtable per module: slots 0..2 are IUnknown (QueryInterface / AddRef / Release, implemented here), the rest are the interface's methods. Every slot is a
/// dynamic stub from <see cref="GuestModule.AllocateStub"/> bound to a managed handler with the exact stdcall pop count (4 * (1 + arguments)). A COM object is a block
/// [vtable pointer][instance id]; the instance id identifies the object in this host and appears in every fault message.
///
/// IUnknown: one reference count per object (starts at 1 for the creator's reference, which is the one handed to the guest). QueryInterface answers IUnknown with the primary face and
/// any IID in a face's IID table (<see cref="GuestComInterface.Iid"/> and <see cref="GuestComInterface.AlsoAnswers"/>) with that face; everything else sets *ppv = NULL and returns E_NOINTERFACE.
/// When Release brings the count to zero the object is destroyed exactly once: the destroy callback runs, the block's vtable pointer is switched to a "dead" vtable whose every slot faults naming the
/// object, and the instance id is remembered. The 8-byte block is NOT reused (a stale pointer therefore faults by name instead of aliasing a new object); the memory goes away with the module.
/// Releasing a destroyed object a second time is detected (the dead vtable) and faults. Handlers may run on any guest thread concurrently; reference counts are atomic and destruction is guarded
/// by a single winner, handler state is the handler's own business. <see cref="Dispose"/> (the resolver / context disposal) destroys every object still alive, running their destroy callbacks so host
/// resources (native interface pointers, windows) are released even when the guest never released them.
/// </summary>
public sealed class GuestComHost : IDisposable
{
    private static readonly ConditionalWeakTable<Win32Context, GuestComHost> hosts = new();
    private const int DeadVtableSlots = 256;

    private readonly object gate = new();
    private readonly Dictionary<uint, GuestComFace> faces = [];
    private readonly Dictionary<uint, string> tombstones = [];
    private readonly Dictionary<GuestComInterface, uint> vtables = [];
    private readonly List<GuestComObject> live = [];
    private readonly GuestHeap heap;
    private readonly uint iunknownQi, iunknownAddRef, iunknownRelease;
    private uint deadVtable;
    private int nextId;
    private bool disposed;

    private GuestComHost(Win32Context context, GuestModule module)
    {
        Context = context;
        Module = module;
        Memory = new GuestMemoryAdapter(module.Memory);
        heap = new GuestHeap(Memory, 0, 0, 0);
        iunknownQi = module.AllocateStub(g => QueryInterface(g), 12);
        iunknownAddRef = module.AllocateStub(g => AddRef(g), 4);
        iunknownRelease = module.AllocateStub(g => Release(g), 4);
        stubs.AddRange([iunknownQi, iunknownAddRef, iunknownRelease]);
    }

    private readonly List<uint> stubs = [];

    /// <summary>The COM host of a context/module pair (created on first use). One context serves one module.</summary>
    public static GuestComHost For(Win32Context context, GuestModule module)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(module);
        var host = hosts.GetValue(context, c => { var h = new GuestComHost(c, module); c.AddDisposable(h); return h; });
        if (!ReferenceEquals(host.Module, module)) throw new Win32UsageException("This Win32 context already serves another guest module's COM objects; create one resolver per module.");
        return host;
    }

    public Win32Context Context { get; }
    public GuestModule Module { get; }
    public IWin32Memory Memory { get; }
    /// <summary>Objects created and not yet destroyed.</summary>
    public int LiveObjects { get { lock (gate) return live.Count; } }
    /// <summary>Objects destroyed so far (their blocks are tombstones).</summary>
    public int DestroyedObjects { get { lock (gate) return tombstones.Count; } }

    /// <summary>
    /// Creates an object with reference count 1. <paramref name="interfaces"/>[0] is the primary face (what <see cref="GuestComObject.Address"/> returns); the others are reachable with QueryInterface.
    /// <paramref name="onDestroy"/> runs once when the count reaches zero (or at disposal); throwing from it faults the guest call.
    /// </summary>
    public GuestComObject Create(string name, object? state, IReadOnlyList<GuestComInterface> interfaces, Action<GuestComObject>? onDestroy = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (interfaces.Count == 0) throw new ArgumentException("An object needs at least one interface.", nameof(interfaces));
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GuestComHost));
            var obj = new GuestComObject(++nextId, name, state) { OnDestroy = onDestroy };
            foreach (var iface in interfaces)
            {
                var vtable = VtableFor(iface);
                var block = heap.Alloc(8, true);
                if (block == 0) throw new Win32UsageException("The guest COM heap is exhausted.");
                Memory.WriteUInt32(block, vtable);
                Memory.WriteUInt32(block + 4, (uint)obj.Id);
                var face = new GuestComFace(obj, iface, block);
                obj.AddFace(face);
                faces[block] = face;
            }
            live.Add(obj);
            return obj;
        }
    }

    public GuestComObject Create(string name, object? state, GuestComInterface iface, Action<GuestComObject>? onDestroy = null) => Create(name, state, [iface], onDestroy);

    /// <summary>The object behind a guest interface pointer (live or destroyed), or null when the address is no COM object of this host.</summary>
    public GuestComObject? Find(uint guestInterfacePointer)
    {
        lock (gate) return faces.TryGetValue(guestInterfacePointer, out var f) ? f.Owner : null;
    }

    private uint VtableFor(GuestComInterface iface)
    {
        if (vtables.TryGetValue(iface, out var existing)) return existing;
        var count = 3 + iface.Methods.Count;
        var table = heap.Alloc((uint)(4 * count), true);
        if (table == 0) throw new Win32UsageException("The guest COM heap is exhausted.");
        Memory.WriteUInt32(table, iunknownQi);
        Memory.WriteUInt32(table + 4, iunknownAddRef);
        Memory.WriteUInt32(table + 8, iunknownRelease);
        for (var i = 0; i < iface.Methods.Count; i++)
        {
            var method = iface.Methods[i];
            var captured = method;
            var stub = Module.AllocateStub(g => Dispatch(captured, g), 4u * (uint)(1 + method.ArgCount));
            stubs.Add(stub);
            Memory.WriteUInt32(table + 4u * (uint)(3 + i), stub);
        }
        vtables[iface] = table;
        return table;
    }

    private uint DeadVtable()
    {
        lock (gate)
        {
            if (deadVtable != 0) return deadVtable;
            var stub = Module.AllocateStub(g => throw new GuestComException(DeadMessage(g.Arg(0))), 0);
            stubs.Add(stub);
            var table = heap.Alloc(4 * DeadVtableSlots, true);
            for (var i = 0; i < DeadVtableSlots; i++) Memory.WriteUInt32(table + 4u * (uint)i, stub);
            return deadVtable = table;
        }
    }

    private string DeadMessage(uint self)
    {
        lock (gate)
            return tombstones.TryGetValue(self, out var name)
                ? $"The guest called a method of {name} through interface pointer 0x{self:X8} after the object was destroyed (its last reference was already released: double Release or use after free)."
                : $"The guest called a COM method through 0x{self:X8}, which is not an object of this module.";
    }

    private GuestComObject Resolve(uint self, out GuestComFace face)
    {
        lock (gate)
        {
            if (faces.TryGetValue(self, out face!)) return face.Owner;
        }
        throw new GuestComException($"The guest called a COM method with 'this' = 0x{self:X8}, which is not an object of this module.");
    }

    private uint Dispatch(GuestComMethod method, GuestCall g)
    {
        var obj = Resolve(g.Arg(0), out var face);
        if (!obj.IsAlive) throw new GuestComException(DeadMessage(g.Arg(0)));
        Context.Trace?.Invoke($"[{Environment.CurrentManagedThreadId}] {face.Interface.Name}::{method.Name} ({obj})");
        return method.Handler is { } h ? h(new GuestComCall(this, obj, face, g)) : ComHr.NotImpl;
    }

    private uint QueryInterface(GuestCall g)
    {
        var obj = Resolve(g.Arg(0), out _);
        if (!obj.IsAlive) throw new GuestComException(DeadMessage(g.Arg(0)));
        var riid = g.Arg(1); var ppv = g.Arg(2);
        if (ppv == 0) return ComHr.Pointer;
        Memory.WriteUInt32(ppv, 0);
        if (riid == 0) return ComHr.InvalidArg;
        var iid = new Guid(Memory.ReadBytes(riid, 16));
        GuestComFace? match = iid == ComIids.IUnknown ? obj.Faces[0] : obj.Faces.FirstOrDefault(f => f.Interface.Matches(iid));
        Context.Trace?.Invoke($"[{Environment.CurrentManagedThreadId}] {obj}::QueryInterface({iid}) -> {(match is null ? "E_NOINTERFACE" : match.Interface.Name)}");
        if (match is null) return ComHr.NoInterface;
        if (obj.AddRefCore() == 0) throw new GuestComException(DeadMessage(g.Arg(0)));
        Memory.WriteUInt32(ppv, match.Address);
        return ComHr.Ok;
    }

    private uint AddRef(GuestCall g)
    {
        var obj = Resolve(g.Arg(0), out _);
        var n = obj.IsAlive ? obj.AddRefCore() : 0;
        if (n == 0) throw new GuestComException(DeadMessage(g.Arg(0)));
        return (uint)n;
    }

    private uint Release(GuestCall g)
    {
        var obj = Resolve(g.Arg(0), out _);
        if (!obj.IsAlive) throw new GuestComException(DeadMessage(g.Arg(0)));
        var n = obj.ReleaseCore();
        if (n < 0) throw new GuestComException(DeadMessage(g.Arg(0)));
        if (n == 0) Destroy(obj, true);
        return (uint)n;
    }

    /// <summary>Host-side AddRef of an object (for objects the host hands out twice).</summary>
    public int AddRef(GuestComObject obj) => obj.AddRefCore() is var n && n > 0 ? n : throw new GuestComException($"{obj} is already destroyed.");

    /// <summary>Host-side Release (the host drops a reference it owns). Destroys the object at zero.</summary>
    public int Release(GuestComObject obj)
    {
        var n = obj.ReleaseCore();
        if (n < 0) throw new GuestComException($"{obj} was released more often than it was referenced.");
        if (n == 0) Destroy(obj, true);
        return n;
    }

    private void Destroy(GuestComObject obj, bool runCallback)
    {
        if (!obj.TryMarkDestroyed()) return;
        uint dead = 0;
        try { dead = DeadVtable(); } catch (Exception) when (Module.IsDisposed) { }   // the module is gone: no guest memory left to mark, but the host resources must still be released
        lock (gate)
        {
            live.Remove(obj);
            foreach (var face in obj.Faces)
            {
                tombstones[face.Address] = obj.ToString() + " (" + face.Interface.Name + ")";
                if (dead != 0) try { Memory.WriteUInt32(face.Address, dead); } catch (ObjectDisposedException) { } catch (GuestException) { }
            }
        }
        if (runCallback) obj.OnDestroy?.Invoke(obj);
    }

    /// <summary>Destroys every live object (running destroy callbacks) and returns the stubs to the pool. Safe after the module was disposed.</summary>
    public void Dispose()
    {
        List<GuestComObject> remaining;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            remaining = [.. live];
            remaining.Reverse();   // newest first: dependants (buffers) go before what they were made from (devices)
        }
        foreach (var obj in remaining)
        {
            try { Destroy(obj, true); } catch (Exception) when (Module.IsDisposed) { }   // a destroy callback may touch guest memory that is gone
        }
        if (!Module.IsDisposed)
            foreach (var stub in stubs) { try { Module.ReleaseStub(stub); } catch (GuestStubException) { } catch (ObjectDisposedException) { } }
        try { heap.Dispose(); } catch (ObjectDisposedException) { } catch (GuestException) { }
    }
}
