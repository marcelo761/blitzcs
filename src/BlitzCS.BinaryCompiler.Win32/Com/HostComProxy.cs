using System.Runtime.CompilerServices;

namespace BlitzCS.BinaryCompiler.Win32.Com;

/// <summary>State of a guest COM object that stands for one native host object: the native interface pointer behind each guest face (one native reference each), keyed by IID.</summary>
public sealed class HostComProxy
{
    private readonly Dictionary<Guid, HostComInterface> hosts = [];
    internal HostComProxy(nint identity) { Identity = identity; }
    /// <summary>The host object's IUnknown pointer value (its COM identity; used as a key only).</summary>
    public nint Identity { get; }
    public HostComInterface Host(Guid iid) { lock (hosts) return hosts.TryGetValue(iid, out var h) ? h : throw new GuestComException($"No native interface {iid} behind this object."); }
    internal bool Has(Guid iid) { lock (hosts) return hosts.ContainsKey(iid); }
    internal void Add(Guid iid, HostComInterface host) { lock (hosts) hosts.Add(iid, host); }
    internal void ReleaseAll() { List<HostComInterface> all; lock (hosts) { all = [.. hosts.Values]; hosts.Clear(); } foreach (var h in all) h.Release(); }
}

/// <summary>
/// One guest object per native object. A native interface pointer the host returns (an out parameter, a QueryInterface result) is wrapped by <see cref="Wrap"/>: its identity is its
/// QueryInterface(IID_IUnknown) pointer; if a live guest object already stands for that identity, the guest gets that object (AddRef, the face for the IID) and the extra native reference is
/// dropped; otherwise a new guest object is created. QueryInterface on the guest object asks the host for an unknown IID: E_NOINTERFACE when the host refuses, a new face of the SAME guest object
/// when the host agrees and an adapter table exists, and a named fault when the host implements the interface but no adapter table does. The guest's last Release releases every native reference.
/// </summary>
public sealed class HostComRegistry
{
    private static readonly ConditionalWeakTable<GuestComHost, HostComRegistry> registries = new();
    private readonly object gate = new();
    private readonly Dictionary<nint, GuestComObject> byIdentity = [];
    private readonly GuestComHost com;
    private readonly Func<Guid, GuestComInterface?> tables;

    private HostComRegistry(GuestComHost com, Func<Guid, GuestComInterface?> tables) { this.com = com; this.tables = tables; }

    /// <summary>The registry of a COM host; <paramref name="tables"/> maps an IID to its adapter table (null: none).</summary>
    public static HostComRegistry For(GuestComHost com, Func<Guid, GuestComInterface?> tables) => registries.GetValue(com, c => new HostComRegistry(c, tables));

    /// <summary>Wraps a native pointer for <paramref name="iid"/> whose reference the caller owns (it is consumed). Returns the guest interface pointer, holding one guest reference.</summary>
    public uint Wrap(nint native, Guid iid)
    {
        var iface = tables(iid);
        var host = new HostComInterface(native, iid.ToString());
        if (iface is null)
        {
            host.Release();
            throw new GuestComException($"The host returned an interface {iid} for which no adapter table exists.");
        }
        host.QueryInterface(ComIids.IUnknown, out var identity);
        if (identity != 0) new HostComInterface(identity, "IUnknown").Release();   // the value is the identity; the reference is not needed
        lock (gate)
        {
            if (identity != 0 && byIdentity.TryGetValue(identity, out var existing) && existing.IsAlive && existing.State is HostComProxy known)
            {
                if (known.Has(iid)) host.Release(); else known.Add(iid, host);
                com.AddRef(existing);
                return com.FaceFor(existing, iface).Address;
            }
            var proxy = new HostComProxy(identity);
            proxy.Add(iid, host);
            var obj = com.Create(iface.Name, proxy, [iface], o =>
            {
                lock (gate) if (byIdentity.TryGetValue(proxy.Identity, out var cur) && cur == o) byIdentity.Remove(proxy.Identity);
                proxy.ReleaseAll();
            }, QueryHost);
            if (identity != 0) byIdentity[identity] = obj;
            return obj.Address;
        }
    }

    private GuestComInterface? QueryHost(GuestComObject obj, Guid iid)
    {
        if (obj.State is not HostComProxy proxy) return null;
        HostComInterface any;
        lock (gate) any = proxy.Host(obj.Faces[0].Interface.Iid);
        var hr = any.QueryInterface(iid, out var native);
        if (!ComHr.Succeeded(hr) || native == 0) return null;
        var iface = tables(iid);
        var host = new HostComInterface(native, iid.ToString());
        if (iface is null)
        {
            host.Release();
            throw new GuestComException($"{obj}::QueryInterface({iid}): the host object implements this interface, but no adapter table exists for it.");
        }
        if (proxy.Has(iid)) host.Release(); else proxy.Add(iid, host);
        return iface;
    }
}
