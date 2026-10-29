using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32.DirectX;

/// <summary>
/// IDirectDrawSurface7 over the host's surfaces. Two things do not cross as-is:
/// <list type="bullet">
/// <item><b>Surface memory (Lock/Unlock).</b> The pointer the host's Lock returns is a 64-bit host address. The guest gets a shadow in guest memory with the host's pitch: guest Lock = host Lock
/// (held until the guest unlocks, as on Windows) + a copy of the locked rows into the shadow (skipped for DDLOCK_WRITEONLY); guest Unlock = the rows copied back (skipped for DDLOCK_READONLY) +
/// host Unlock. lpSurface in the guest's DDSURFACEDESC2 is the shadow. Whole-surface DXT1–DXT5 locks use DDSD_LINEARSIZE and copy the compressed bytes unchanged; other FOURCC formats and sub-byte RGB formats are refused by name.</item>
/// <item><b>DDBLTFX</b> (100 bytes on the guest, 128 on the host: five unions of a DWORD and a surface pointer). The DWORD members are copied; the surface-pointer forms (z-buffer and alpha
/// overrides, pattern) are refused by name.</item>
/// </list>
/// </summary>
public static unsafe class DirectDrawSurfaces
{
    public const uint DdLockReadOnly = 0x10, DdLockWriteOnly = 0x20;
    public const int GuestBltFx = 100, HostBltFx = 128;
    private const uint DdBltZBufferDestOverride = 0x100000, DdBltZBufferSrcOverride = 0x400000, DdBltAlphaDestSurfaceOverride = 0x1000, DdBltAlphaSrcSurfaceOverride = 0x4000;

    private sealed class Shadow
    {
        public required IWin32Allocation Buffer;
        public required nint HostBits;
        public required int Pitch, Rows, RowBytes;
        public required uint Flags;
        public required byte[]? Rect;   // the guest's RECT (null = whole surface)
    }

    private sealed class Locks { public readonly List<Shadow> Active = []; }
    private static readonly ConditionalWeakTable<GuestComObject, Locks> locks = new();

    private sealed class SurfaceNode
    {
        public GuestComObject? Root;
        public readonly HashSet<GuestComObject> Attached = [];
        public bool HasDestroyHook;
    }

    private static readonly ConditionalWeakTable<GuestComObject, SurfaceNode> nodes = new();
    private static readonly object hierarchyGate = new();

    /// <summary>Guest DDBLTFX (100 bytes) to host (128 bytes): the five DWORD/pointer unions are 4 bytes on the guest and 8-byte aligned 8 bytes on the host; their DWORD members are copied.</summary>
    public static void BltFxToHost(ReadOnlySpan<byte> g, Span<byte> h)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(g) != GuestBltFx) throw new ComArgumentException($"DDBLTFX.dwSize is {BinaryPrimitives.ReadUInt32LittleEndian(g)}, not {GuestBltFx}.");
        h[..HostBltFx].Clear();
        g[..40].CopyTo(h);                                                   // dwSize .. dwZDestConstBitDepth
        BinaryPrimitives.WriteUInt32LittleEndian(h, HostBltFx);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], BinaryPrimitives.ReadUInt32LittleEndian(g[40..]));   // dwZDestConst (DWORD form)
        g.Slice(44, 4).CopyTo(h[48..]);                                      // dwZSrcConstBitDepth
        BinaryPrimitives.WriteUInt32LittleEndian(h[56..], BinaryPrimitives.ReadUInt32LittleEndian(g[48..]));   // dwZSrcConst
        g.Slice(52, 16).CopyTo(h[64..]);                                     // dwAlphaEdgeBlendBitDepth, dwAlphaEdgeBlend, dwReserved, dwAlphaDestConstBitDepth
        BinaryPrimitives.WriteUInt32LittleEndian(h[80..], BinaryPrimitives.ReadUInt32LittleEndian(g[68..]));   // dwAlphaDestConst
        g.Slice(72, 4).CopyTo(h[88..]);                                      // dwAlphaSrcConstBitDepth
        BinaryPrimitives.WriteUInt32LittleEndian(h[96..], BinaryPrimitives.ReadUInt32LittleEndian(g[76..]));   // dwAlphaSrcConst
        BinaryPrimitives.WriteUInt32LittleEndian(h[104..], BinaryPrimitives.ReadUInt32LittleEndian(g[80..]));  // dwFillColor / dwFillDepth / dwFillPixel
        g.Slice(84, 16).CopyTo(h[112..]);                                    // ddckDestColorkey, ddckSrcColorkey
    }

    private static readonly Guid Surface7 = DdConst.IidDirectDrawSurface7;
    private static GuestComInterface? table;
    public static GuestComInterface Table => table ??= Build();

    private static HostComInterface Host(GuestComObject o) => ((HostComProxy)o.State!).Host(Surface7);
    private static ComArg Surface(bool optional) => DirectDrawAdapters.InObject(Surface7, optional);
    private static readonly ComArg Rect = ComArgs.InStruct(16, optional: true);

    private static ComArg BltFx(Func<uint[], uint> flagsOf) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return 0;
        var flags = flagsOf([s.Call.Arg(0), s.Call.Arg(1), s.Call.Arg(2), s.Call.Arg(3), s.Call.Arg(4)]);
        if ((flags & (DdBltZBufferDestOverride | DdBltZBufferSrcOverride | DdBltAlphaDestSurfaceOverride | DdBltAlphaSrcSurfaceOverride)) != 0)
            throw new Win32UsageException("IDirectDrawSurface7::Blt with a z-buffer or alpha surface override in DDBLTFX (a surface pointer inside the structure) is not bridged.");
        var native = s.Native.AllocZeroed(HostBltFx);
        BltFxToHost(s.Mem.ReadBytes(v, GuestBltFx), new Span<byte>((void*)native, HostBltFx));
        return native;
    });

    private static GuestComInterface Build()
    {
        var i = new GuestComInterface("IDirectDrawSurface7", Surface7);
        Func<GuestComObject, HostComInterface> host = Host;
        i.Add(new GuestComMethod("AddAttachedSurface", 1, AddAttachedSurface));
        i.Add(new HostComMethod("AddOverlayDirtyRect", 4, ComArgs.InStruct(16)).ToGuest(host));
        i.Add(new HostComMethod("Blt", 5, Rect, Surface(true), Rect, ComArgs.Value, BltFx(a => a[3])).ToGuest(host));
        i.Absent("BltBatch", 3, "DDBLTBATCH (an array of structures with surface pointers) has no adapter row; DirectDraw does not implement it either");
        i.Add(new HostComMethod("BltFast", 7, ComArgs.Value, ComArgs.Value, Surface(true), Rect, ComArgs.Value).ToGuest(host));
        i.Add(new GuestComMethod("DeleteAttachedSurface", 2, DeleteAttachedSurface));
        i.Add(new GuestComMethod("EnumAttachedSurfaces", 2, EnumAttachedSurfaces));
        i.Absent("EnumOverlayZOrders", 3, "overlays have no adapter rows");
        i.Add(new HostComMethod("Flip", 11, Surface(true), ComArgs.Value).ToGuest(host));
        i.Add(new GuestComMethod("GetAttachedSurface", 2, GetAttachedSurface));
        i.Add(new HostComMethod("GetBltStatus", 13, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetCaps", 14, ComArgs.OutBytes(16)).ToGuest(host));
        i.Add(new HostComMethod("GetClipper", 15, DirectDrawAdapters.OutObject(DdConst.IidDirectDrawClipper)).ToGuest(host));
        i.Add(new HostComMethod("GetColorKey", 16, ComArgs.Value, ComArgs.OutBytes(8)).ToGuest(host));
        i.Add(new GuestComMethod("GetDC", 1, c =>
        {
            if (c.Arg(0) == 0) return DdConst.DdErrInvalidParams;
            nint hdc = 0;
            var hr = Host(c.Object).Invoke(17, (nint)(&hdc));
            WindowProcBridge.ThrowPendingCallbackFault();
            if (ComHr.Succeeded(hr)) c.Mem.WriteUInt32(c.Arg(0), HostHandles.ToGuest(hdc, "IDirectDrawSurface7::GetDC"));
            return hr;
        }));
        i.Add(new HostComMethod("GetFlipStatus", 18, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetOverlayPosition", 19, ComArgs.OutU32(), ComArgs.OutU32()).ToGuest(host));
        i.Absent("GetPalette", 1, "IDirectDrawPalette has no adapter table");
        i.Add(new HostComMethod("GetPixelFormat", 21, ComArgs.InOutStruct(DdLayouts.PixelFormat)).ToGuest(host));
        i.Add(new HostComMethod("GetSurfaceDesc", 22, DirectDrawAdapters.SurfaceDesc(inOut: true)).ToGuest(host));
        i.Absent("Initialize", 2, "a surface made by CreateSurface is already initialised");
        i.Add(new HostComMethod("IsLost", 24).ToGuest(host));
        i.Add(new GuestComMethod("Lock", 4, Lock));
        i.Add(new HostComMethod("ReleaseDC", 26, HostArgs.Handle).ToGuest(host));
        i.Add(new HostComMethod("Restore", 27).ToGuest(host));
        i.Add(new HostComMethod("SetClipper", 28, DirectDrawAdapters.InObject(DdConst.IidDirectDrawClipper, true)).ToGuest(host));
        i.Add(new HostComMethod("SetColorKey", 29, ComArgs.Value, ComArgs.InStruct(8, optional: true)).ToGuest(host));
        i.Add(new HostComMethod("SetOverlayPosition", 30, ComArgs.Signed, ComArgs.Signed).ToGuest(host));
        i.Absent("SetPalette", 1, "IDirectDrawPalette has no adapter table");
        i.Add(new GuestComMethod("Unlock", 1, Unlock));
        i.Absent("UpdateOverlay", 5, "overlays have no adapter rows");
        i.Add(new HostComMethod("UpdateOverlayDisplay", 34, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("UpdateOverlayZOrder", 35, ComArgs.Value, Surface(true)).ToGuest(host));
        i.Add(new HostComMethod("GetDDInterface", 36, DirectDrawAdapters.OutObject(DdConst.IidDirectDraw7)).ToGuest(host));
        i.Add(new HostComMethod("PageLock", 37, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("PageUnlock", 38, ComArgs.Value).ToGuest(host));
        i.Absent("SetSurfaceDesc", 2, "client-supplied surface memory (DDSD_LPSURFACE) is not bridged");
        i.Absent("SetPrivateData", 4, "private data may hold a guest IUnknown pointer (DDSPD_IUNKNOWNPOINTER) and has no adapter row");
        i.Absent("GetPrivateData", 3, "see SetPrivateData");
        i.Add(new HostComMethod("FreePrivateData", 42, ComArgs.Guid(optional: false)).ToGuest(host));
        i.Add(new HostComMethod("GetUniquenessValue", 43, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("ChangeUniquenessValue", 44).ToGuest(host));
        i.Add(new HostComMethod("SetPriority", 45, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetPriority", 46, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("SetLOD", 47, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetLOD", 48, ComArgs.OutU32()).ToGuest(host));
        return i;
    }

    private static void AttachChild(GuestComHost host, GuestComObject parent, uint childGuestAddr)
    {
        var child = host.Find(childGuestAddr);
        if (child is null || child == parent) return;
        lock (hierarchyGate)
        {
            var root = nodes.TryGetValue(parent, out var pNode) && pNode.Root is { } r && r.IsAlive ? r : parent;
            if (child == root) return;
            var rootNode = nodes.GetValue(root, _ => new SurfaceNode());
            var childNode = nodes.GetValue(child, _ => new SurfaceNode());
            childNode.Root = root;
            if (rootNode.Attached.Add(child))
            {
                host.AddRef(child);
                if (!rootNode.HasDestroyHook)
                {
                    rootNode.HasDestroyHook = true;
                    Action<GuestComObject> releaseChildren = _ =>
                    {
                        List<GuestComObject> toRelease;
                        lock (hierarchyGate)
                        {
                            toRelease = [.. rootNode.Attached];
                            rootNode.Attached.Clear();
                        }
                        foreach (var attached in toRelease)
                        {
                            try { if (attached.IsAlive) host.Release(attached); } catch { }
                        }
                    };
                    // The native complex surface must still be alive when its attached
                    // surfaces release their native references. The proxy's callback follows.
                    root.OnDestroy = releaseChildren + root.OnDestroy;
                }
            }
        }
    }

    private static void DetachChild(GuestComHost host, GuestComObject parent, GuestComObject child)
    {
        lock (hierarchyGate)
        {
            var root = nodes.TryGetValue(parent, out var pNode) && pNode.Root is { } r && r.IsAlive ? r : parent;
            if (nodes.TryGetValue(root, out var rootNode) && rootNode.Attached.Remove(child))
            {
                try { if (child.IsAlive) host.Release(child); } catch { }
            }
        }
    }

    private static uint AddAttachedSurface(GuestComCall c)
    {
        uint attachedAddr = c.Arg(0);
        if (attachedAddr == 0) return DdConst.DdErrInvalidParams;
        var attachedObj = c.Host.Find(attachedAddr);
        if (attachedObj is null) return DdConst.DdErrInvalidParams;
        var hr = Host(c.Object).Invoke(3, Host(attachedObj).Pointer);
        WindowProcBridge.ThrowPendingCallbackFault();
        if (ComHr.Succeeded(hr))
        {
            AttachChild(c.Host, c.Object, attachedAddr);
        }
        return hr;
    }

    private static uint DeleteAttachedSurface(GuestComCall c)
    {
        uint flags = c.Arg(0), attachedAddr = c.Arg(1);
        nint nativeAttached = 0;
        GuestComObject? attachedObj = null;
        if (attachedAddr != 0)
        {
            attachedObj = c.Host.Find(attachedAddr);
            if (attachedObj is null) return DdConst.DdErrInvalidParams;
            nativeAttached = Host(attachedObj).Pointer;
        }
        var hr = Host(c.Object).Invoke(8, (nint)flags, nativeAttached);
        WindowProcBridge.ThrowPendingCallbackFault();
        if (ComHr.Succeeded(hr) && attachedObj is not null)
        {
            DetachChild(c.Host, c.Object, attachedObj);
        }
        return hr;
    }

    private static uint GetAttachedSurface(GuestComCall c)
    {
        uint capsPtr = c.Arg(0), outSurfPtr = c.Arg(1);
        if (outSurfPtr == 0) return ComHr.Pointer;
        c.Mem.WriteUInt32(outSurfPtr, 0);
        if (capsPtr == 0) return DdConst.DdErrInvalidParams;
        using var scope = new NativeScope();
        var nativeCaps = scope.Copy(c.Mem.ReadBytes(capsPtr, 16));
        nint nativeOut = 0;
        var hr = Host(c.Object).Invoke(12, nativeCaps, (nint)(&nativeOut));
        WindowProcBridge.ThrowPendingCallbackFault();
        if (!ComHr.Succeeded(hr) || nativeOut == 0) return hr;
        var registry = HostComRegistry.For(c.Host, DirectXTables.Find);
        var guestAddr = registry.Wrap(nativeOut, Surface7);
        AttachChild(c.Host, c.Object, guestAddr);
        c.Mem.WriteUInt32(outSurfPtr, guestAddr);
        return hr;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SurfacesCallback(nint surface, nint desc, nint context);

    /// <summary>EnumAttachedSurfaces(context, callback): each surface the host enumerates arrives with a reference the callback owns, so it is wrapped (identity kept) with that reference.</summary>
    private static uint EnumAttachedSurfaces(GuestComCall c)
    {
        uint context = c.Arg(0), callback = c.Arg(1);
        if (callback == 0) return DdConst.DdErrInvalidParams;
        var call = c; var mem = c.Mem;
        var registry = HostComRegistry.For(c.Host, DirectXTables.Find);
        Exception? failure = null;
        using var scratch = mem.Allocate(DdLayouts.GuestSurfaceDesc2);
        SurfacesCallback native = (surface, desc, _) =>
        {
            if (failure is not null) { if (surface != 0) new HostComInterface(surface, "surface").Release(); return 0; }
            try
            {
                var guest = registry.Wrap(surface, Surface7);
                AttachChild(call.Host, call.Object, guest);
                Span<byte> g = stackalloc byte[DdLayouts.GuestSurfaceDesc2];
                DdLayouts.SurfaceDescToGuest(new ReadOnlySpan<byte>((void*)desc, DdLayouts.HostSurfaceDesc2), g);
                mem.Write(scratch.Address, g);
                return (int)call.CallGuest(callback, guest, scratch.Address, context);
            }
            catch (Exception e) { failure = e; return 0; }
        };
        var hr = Host(c.Object).Invoke(9, 0, Marshal.GetFunctionPointerForDelegate(native));
        GC.KeepAlive(native);
        if (failure is not null) throw new HostCallbackException("An IDirectDrawSurface7::EnumAttachedSurfaces callback failed: " + failure.Message, failure);
        return hr;
    }

    private static uint Lock(GuestComCall c)
    {
        uint rect = c.Arg(0), desc = c.Arg(1), flags = c.Arg(2), evt = c.Arg(3);
        if (desc == 0 || evt != 0) return DdConst.DdErrInvalidParams;
        var guestDesc = c.Mem.ReadBytes(desc, DdLayouts.GuestSurfaceDesc2);
        if (BinaryPrimitives.ReadUInt32LittleEndian(guestDesc) != DdLayouts.GuestSurfaceDesc2) return DdConst.DdErrInvalidParams;
        using var scope = new NativeScope();
        var nativeDesc = scope.AllocZeroed(DdLayouts.HostSurfaceDesc2);
        *(uint*)nativeDesc = DdLayouts.HostSurfaceDesc2;
        var guestRect = rect == 0 ? null : c.Mem.ReadBytes(rect, 16);
        var nativeRect = guestRect is null ? 0 : scope.Copy(guestRect);
        var host = Host(c.Object);
        var hr = host.Invoke(25, nativeRect, nativeDesc, (nint)flags, 0);
        WindowProcBridge.ThrowPendingCallbackFault();
        if (!ComHr.Succeeded(hr)) return hr;
        var h = new ReadOnlySpan<byte>((void*)nativeDesc, DdLayouts.HostSurfaceDesc2);
        try
        {
            var bits = (nint)BinaryPrimitives.ReadInt64LittleEndian(h[40..]);
            var pitch = BinaryPrimitives.ReadInt32LittleEndian(h[16..]);
            var bitCount = BinaryPrimitives.ReadUInt32LittleEndian(h[(80 + 12)..]);   // ddpfPixelFormat.dwRGBBitCount
            var pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(h[(80 + 4)..]);
            int width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[12..])), height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[8..]));
            int rowBytes, rows;
            if ((pfFlags & 0x4) != 0) // DDPF_FOURCC: the union holds a total byte size, not a scanline pitch.
            {
                var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(h[(80 + 8)..]);
                var blockBytes = fourCc switch
                {
                    0x31545844 => 8, // DXT1: one 8-byte block per 4x4 texels
                    0x32545844 or 0x33545844 or 0x34545844 or 0x35545844 => 16, // DXT2–DXT5
                    _ => throw new Win32UsageException($"IDirectDrawSurface7::Lock: unsupported FOURCC 0x{fourCc:X} (pixel format flags 0x{pfFlags:X}); only DXT1–DXT5 compressed formats are bridged."),
                };
                var fullRect = guestRect is null || (BinaryPrimitives.ReadInt32LittleEndian(guestRect) == 0
                    && BinaryPrimitives.ReadInt32LittleEndian(guestRect.AsSpan(4)) == 0
                    && BinaryPrimitives.ReadInt32LittleEndian(guestRect.AsSpan(8)) == width
                    && BinaryPrimitives.ReadInt32LittleEndian(guestRect.AsSpan(12)) == height);
                if (!fullRect || (BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) & 0x80000) == 0) // DDSD_LINEARSIZE
                    throw new Win32UsageException("IDirectDrawSurface7::Lock: compressed surfaces require a whole-surface lock with DDSD_LINEARSIZE.");
                var minimumBytes = checked(((width + 3) / 4) * ((height + 3) / 4) * blockBytes);
                if (width <= 0 || height <= 0 || pitch < minimumBytes)
                    throw new Win32UsageException($"IDirectDrawSurface7::Lock: unusable compressed layout ({width}x{height}, linear size {pitch}, minimum {minimumBytes}).");
                rowBytes = pitch;
                rows = 1;
            }
            else
            {
                if (bitCount < 8 || bitCount % 8 != 0)
                    throw new Win32UsageException($"IDirectDrawSurface7::Lock on a surface with pixel format flags 0x{pfFlags:X} and {bitCount} bits per pixel: only whole-byte RGB formats are bridged.");
                if (guestRect is not null)
                {
                    width = BinaryPrimitives.ReadInt32LittleEndian(guestRect.AsSpan(8)) - BinaryPrimitives.ReadInt32LittleEndian(guestRect);
                    height = BinaryPrimitives.ReadInt32LittleEndian(guestRect.AsSpan(12)) - BinaryPrimitives.ReadInt32LittleEndian(guestRect.AsSpan(4));
                }
                rowBytes = checked(width * (int)(bitCount / 8));
                rows = height;
            }
            if (bits == 0 || pitch < rowBytes || rows <= 0 || rowBytes <= 0) throw new Win32UsageException($"IDirectDrawSurface7::Lock: the host returned an unusable lock (bits 0x{bits:X}, pitch/linear size {pitch}, {width}x{height}).");
            var buffer = c.Mem.Allocate(checked(pitch * rows));
            if ((flags & DdLockWriteOnly) == 0)
                for (var y = 0; y < rows; y++) c.Mem.Write(buffer.Address + (uint)(y * pitch), new ReadOnlySpan<byte>((void*)(bits + (nint)y * pitch), rowBytes));
            lock (locks) locks.GetValue(c.Object, _ => new Locks()).Active.Add(new Shadow { Buffer = buffer, HostBits = bits, Pitch = pitch, Rows = rows, RowBytes = rowBytes, Flags = flags, Rect = guestRect });
            Span<byte> g = stackalloc byte[DdLayouts.GuestSurfaceDesc2];
            DdLayouts.SurfaceDescToGuest(h, g);
            BinaryPrimitives.WriteUInt32LittleEndian(g[4..], BinaryPrimitives.ReadUInt32LittleEndian(h[4..]));   // keep DDSD_LPSURFACE: lpSurface is valid (the shadow)
            BinaryPrimitives.WriteUInt32LittleEndian(g[36..], buffer.Address);
            c.Mem.Write(desc, g);
            return hr;
        }
        catch
        {
            host.Invoke(32, nativeRect);
            throw;
        }
    }

    private static uint Unlock(GuestComCall c)
    {
        var rect = c.Arg(0);
        var guestRect = rect == 0 ? null : c.Mem.ReadBytes(rect, 16);
        Shadow? shadow;
        lock (locks)
        {
            var active = locks.GetValue(c.Object, _ => new Locks()).Active;
            shadow = guestRect is null ? (active.Count == 1 ? active[0] : null) : active.FirstOrDefault(s => s.Rect is { } r && r.AsSpan().SequenceEqual(guestRect));
            if (shadow is not null) active.Remove(shadow);
        }
        if (shadow is null) return 0x887601AA;   // DDERR_NOTLOCKED
        try
        {
            if ((shadow.Flags & DdLockReadOnly) == 0)
                for (var y = 0; y < shadow.Rows; y++) c.Mem.Read(shadow.Buffer.Address + (uint)(y * shadow.Pitch), new Span<byte>((void*)(shadow.HostBits + (nint)y * shadow.Pitch), shadow.RowBytes));
        }
        finally { shadow.Buffer.Dispose(); }
        using var scope = new NativeScope();
        var hr = Host(c.Object).Invoke(32, shadow.Rect is null ? 0 : scope.Copy(shadow.Rect));
        WindowProcBridge.ThrowPendingCallbackFault();
        return hr;
    }
}
