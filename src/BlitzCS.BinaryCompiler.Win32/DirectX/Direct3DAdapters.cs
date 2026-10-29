using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32.DirectX;

/// <summary>
/// Direct3D 7 (<c>IDirect3DDevice7</c> and <c>IDirect3DVertexBuffer7</c>) over the host's ddraw.dll.
/// <list type="bullet">
/// <item><b>Clear (slot 10).</b> Passes <c>D3DVALUE dvZ</c> (<c>float</c>) by value; invoked through a typed unmanaged function pointer so the Win64 ABI places the float argument accurately.</item>
/// <item><b>Vertex buffer Lock/Unlock.</b> Like <see cref="DirectDrawSurfaces"/>, the host's 64-bit vertex memory pointer cannot be handed to 32-bit guest code: <c>Lock</c> allocates a shadow in
/// guest low memory initialized from the host buffer, and <c>Unlock</c> copies the shadow back (unless <c>DDLOCK_READONLY</c>) before unlocking the host buffer.</item>
/// <item><b>Immediate primitive calls.</b> <c>DrawPrimitive</c> and <c>DrawIndexedPrimitive</c> copy the guest vertex array (<c>vertexCount * FvfStride(fvf)</c> bytes) and 16-bit index array into
/// native scratch memory for the duration of the call.</item>
/// </list>
/// </summary>
public static unsafe class Direct3DAdapters
{
    private const uint DdLockReadOnly = DirectDrawSurfaces.DdLockReadOnly;
    private const uint D3dFvfPositionMask = 0x00E, D3dFvfXyz = 0x002, D3dFvfXyzRhw = 0x004, D3dFvfXyzB1 = 0x006, D3dFvfXyzB2 = 0x008, D3dFvfXyzB3 = 0x00A, D3dFvfXyzB4 = 0x00C, D3dFvfXyzB5 = 0x00E;
    private const uint D3dFvfNormal = 0x010, D3dFvfReserved1 = 0x020, D3dFvfDiffuse = 0x040, D3dFvfSpecular = 0x080, D3dFvfTexCountMask = 0x0F00;

    private sealed class VbShadow
    {
        public required IWin32Allocation Buffer;
        public required nint HostBits;
        public required int Size;
        public required uint Flags;
    }

    private sealed class VbLocks { public readonly List<VbShadow> Active = []; }
    private static readonly ConditionalWeakTable<GuestComObject, VbLocks> vbLocks = new();

    private static GuestComInterface? deviceTable, vbTable;
    public static GuestComInterface DeviceTable => deviceTable ??= BuildDevice();
    public static GuestComInterface VertexBufferTable => vbTable ??= BuildVertexBuffer();

    private static Func<GuestComObject, HostComInterface> HostOf(Guid iid) => o => ((HostComProxy)o.State!).Host(iid);

    /// <summary>Computes the byte stride of one Direct3D 7 flexible vertex format (<paramref name="fvf"/>) vertex.</summary>
    public static int FvfStride(uint fvf)
    {
        var stride = (fvf & D3dFvfPositionMask) switch
        {
            D3dFvfXyz => 12,
            D3dFvfXyzRhw => 16,
            D3dFvfXyzB1 => 16,
            D3dFvfXyzB2 => 20,
            D3dFvfXyzB3 => 24,
            D3dFvfXyzB4 => 28,
            D3dFvfXyzB5 => 32,
            _ => throw new ComArgumentException($"FVF 0x{fvf:X} has an invalid position mask (0x{fvf & D3dFvfPositionMask:X})."),
        };
        if ((fvf & D3dFvfNormal) != 0)
        {
            if ((fvf & D3dFvfPositionMask) == D3dFvfXyzRhw) throw new ComArgumentException($"FVF 0x{fvf:X} combines D3DFVF_XYZRHW and D3DFVF_NORMAL.");
            stride += 12;
        }
        if ((fvf & D3dFvfReserved1) != 0) stride += 4;
        if ((fvf & D3dFvfDiffuse) != 0) stride += 4;
        if ((fvf & D3dFvfSpecular) != 0) stride += 4;
        var texCount = (int)((fvf & D3dFvfTexCountMask) >> 8);
        if (texCount > 8) throw new ComArgumentException($"FVF 0x{fvf:X} specifies {texCount} texture coordinate sets (maximum 8).");
        for (var t = 0; t < texCount; t++)
        {
            stride += ((fvf >> (16 + 2 * t)) & 0x3) switch
            {
                0 => 8,    // D3DFVF_TEXTUREFORMAT2 (2 floats)
                1 => 12,   // D3DFVF_TEXTUREFORMAT3 (3 floats)
                2 => 16,   // D3DFVF_TEXTUREFORMAT4 (4 floats)
                _ => 4,    // D3DFVF_TEXTUREFORMAT1 (1 float)
            };
        }
        return stride;
    }

    private static ComArg Vertices(int fvfIndex, int countIndex) => new DelegateComArg((s, v) =>
    {
        if (v == 0) throw new ComArgumentException("A vertex array pointer must not be NULL.");
        var stride = FvfStride(s.Arg(fvfIndex));
        var count = checked((int)s.Arg(countIndex));
        if (count < 0 || count > 1 << 20) throw new ComArgumentException($"A vertex count of {count} is not plausible.");
        return s.Native.CopyFromGuest(s.Mem, v, checked(count * stride));
    });

    // ---- IDirect3DDevice7 ----

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FormatsCallback(nint pixelFormat, nint context);

    private static GuestComInterface BuildDevice()
    {
        const string noStrided = "D3DDRAWPRIMITIVESTRIDEDDATA (12 pointer+stride pairs) has no adapter row";
        var i = new GuestComInterface("IDirect3DDevice7", DdConst.IidDirect3DDevice7);
        var host = HostOf(DdConst.IidDirect3DDevice7);
        var surface = DirectDrawAdapters.InObject(DdConst.IidDirectDrawSurface7, optional: false);
        var optSurface = DirectDrawAdapters.InObject(DdConst.IidDirectDrawSurface7, optional: true);
        var outSurface = DirectDrawAdapters.OutObject(DdConst.IidDirectDrawSurface7);
        var vb = DirectDrawAdapters.InObject(DdConst.IidDirect3DVertexBuffer7, optional: false);

        i.Add(new HostComMethod("GetCaps", 3, ComArgs.OutBytes(DdLayouts.D3dDeviceDesc7)).ToGuest(host));
        i.Add(new GuestComMethod("EnumTextureFormats", 2, c => EnumTextureFormats(c, host(c.Object))));
        i.Add(new HostComMethod("BeginScene", 5).ToGuest(host));
        i.Add(new HostComMethod("EndScene", 6).ToGuest(host));
        i.Add(new HostComMethod("GetDirect3D", 7, DirectDrawAdapters.OutObject(DdConst.IidDirect3D7)).ToGuest(host));
        i.Add(new HostComMethod("SetRenderTarget", 8, surface, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetRenderTarget", 9, outSurface).ToGuest(host));
        i.Add(new GuestComMethod("Clear", 6, c => Clear(c, host(c.Object))));
        i.Add(new HostComMethod("SetTransform", 11, ComArgs.Value, ComArgs.InStruct(DdLayouts.D3dMatrix)).ToGuest(host));
        i.Add(new HostComMethod("GetTransform", 12, ComArgs.Value, ComArgs.OutBytes(DdLayouts.D3dMatrix)).ToGuest(host));
        i.Add(new HostComMethod("SetViewport", 13, ComArgs.InStruct(DdLayouts.D3dViewport7)).ToGuest(host));
        i.Add(new HostComMethod("MultiplyTransform", 14, ComArgs.Value, ComArgs.InStruct(DdLayouts.D3dMatrix)).ToGuest(host));
        i.Add(new HostComMethod("GetViewport", 15, ComArgs.OutBytes(DdLayouts.D3dViewport7)).ToGuest(host));
        i.Add(new HostComMethod("SetMaterial", 16, ComArgs.InStruct(DdLayouts.D3dMaterial7)).ToGuest(host));
        i.Add(new HostComMethod("GetMaterial", 17, ComArgs.OutBytes(DdLayouts.D3dMaterial7)).ToGuest(host));
        i.Add(new HostComMethod("SetLight", 18, ComArgs.Value, ComArgs.InStruct(DdLayouts.D3dLight7)).ToGuest(host));
        i.Add(new HostComMethod("GetLight", 19, ComArgs.Value, ComArgs.OutBytes(DdLayouts.D3dLight7)).ToGuest(host));
        i.Add(new HostComMethod("SetRenderState", 20, ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetRenderState", 21, ComArgs.Value, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("BeginStateBlock", 22).ToGuest(host));
        i.Add(new HostComMethod("EndStateBlock", 23, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("PreLoad", 24, surface).ToGuest(host));
        i.Add(new HostComMethod("DrawPrimitive", 25, ComArgs.Value, ComArgs.Value, Vertices(1, 3), ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("DrawIndexedPrimitive", 26, ComArgs.Value, ComArgs.Value, Vertices(1, 3), ComArgs.Value, HostArgs.CountedArray(5, 2, optional: false), ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("SetClipStatus", 27, ComArgs.InStruct(DdLayouts.D3dClipStatus)).ToGuest(host));
        i.Add(new HostComMethod("GetClipStatus", 28, ComArgs.OutBytes(DdLayouts.D3dClipStatus)).ToGuest(host));
        i.Absent("DrawPrimitiveStrided", 5, noStrided);
        i.Absent("DrawIndexedPrimitiveStrided", 7, noStrided);
        i.Add(new HostComMethod("DrawPrimitiveVB", 31, ComArgs.Value, vb, ComArgs.Value, ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("DrawIndexedPrimitiveVB", 32, ComArgs.Value, vb, ComArgs.Value, ComArgs.Value, HostArgs.CountedArray(5, 2, optional: false), ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("ComputeSphereVisibility", 33, HostArgs.CountedArray(2, 12, optional: false), HostArgs.CountedArray(2, 4, optional: false), ComArgs.Value, ComArgs.Value, HostArgs.OutArray(a => (int)a[2], 4)).ToGuest(host));
        i.Add(new HostComMethod("GetTexture", 34, ComArgs.Value, outSurface).ToGuest(host));
        i.Add(new HostComMethod("SetTexture", 35, ComArgs.Value, optSurface).ToGuest(host));
        i.Add(new HostComMethod("GetTextureStageState", 36, ComArgs.Value, ComArgs.Value, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("SetTextureStageState", 37, ComArgs.Value, ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("ValidateDevice", 38, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("ApplyStateBlock", 39, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("CaptureStateBlock", 40, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("DeleteStateBlock", 41, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("CreateStateBlock", 42, ComArgs.Value, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("Load", 43, surface, ComArgs.InStruct(8, optional: true), surface, ComArgs.InStruct(16, optional: true), ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("LightEnable", 44, ComArgs.Value, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetLightEnable", 45, ComArgs.Value, ComArgs.OutU32()).ToGuest(host));
        i.Add(new HostComMethod("SetClipPlane", 46, ComArgs.Value, ComArgs.InStruct(16)).ToGuest(host));
        i.Add(new HostComMethod("GetClipPlane", 47, ComArgs.Value, ComArgs.OutBytes(16)).ToGuest(host));
        i.Add(new HostComMethod("GetInfo", 48, ComArgs.Value, HostArgs.OutArray(a => (int)a[2], 1), ComArgs.Value).ToGuest(host));
        return i;
    }

    private static uint EnumTextureFormats(GuestComCall c, HostComInterface host)
    {
        uint callback = c.Arg(0), context = c.Arg(1);
        if (callback == 0) return DdConst.DdErrInvalidParams;
        var mem = c.Mem; var call = c;
        Exception? failure = null;
        using var scratch = mem.Allocate(DdLayouts.PixelFormat);
        FormatsCallback native = (format, _) =>
        {
            if (failure is not null) return 0;
            try
            {
                mem.Write(scratch.Address, new ReadOnlySpan<byte>((void*)format, DdLayouts.PixelFormat));
                return (int)call.CallGuest(callback, scratch.Address, context);
            }
            catch (Exception e) { failure = e; return 0; }
        };
        var hr = host.Invoke(4, Marshal.GetFunctionPointerForDelegate(native), 0);
        GC.KeepAlive(native);
        if (failure is not null) throw new HostCallbackException("An IDirect3DDevice7::EnumTextureFormats callback failed: " + failure.Message, failure);
        return hr;
    }

    private static uint Clear(GuestComCall c, HostComInterface host)
    {
        uint count = c.Arg(0), rects = c.Arg(1), flags = c.Arg(2), color = c.Arg(3), zBits = c.Arg(4), stencil = c.Arg(5);
        if ((count > 0 && rects == 0) || count > 65536) return DdConst.DdErrInvalidParams;
        using var scope = new NativeScope();
        var nativeRects = count == 0 || rects == 0 ? 0 : scope.CopyFromGuest(c.Mem, rects, checked((int)count * 16));
        var self = host.Pointer;
        var f = (delegate* unmanaged<nint, uint, nint, uint, uint, float, uint, int>)(*(nint**)self)[10];
        uint hr;
        if (c.Profile.Enabled)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { hr = (uint)f(self, count, nativeRects, flags, color, BitConverter.UInt32BitsToSingle(zBits), stencil); }
            finally { c.Profile.RecordNativeCall(System.Diagnostics.Stopwatch.GetTimestamp() - start); }
        }
        else hr = (uint)f(self, count, nativeRects, flags, color, BitConverter.UInt32BitsToSingle(zBits), stencil);
        WindowProcBridge.ThrowPendingCallbackFault();
        return hr;
    }

    // ---- IDirect3DVertexBuffer7 ----

    private static GuestComInterface BuildVertexBuffer()
    {
        var i = new GuestComInterface("IDirect3DVertexBuffer7", DdConst.IidDirect3DVertexBuffer7);
        var host = HostOf(DdConst.IidDirect3DVertexBuffer7);
        var device = DirectDrawAdapters.InObject(DdConst.IidDirect3DDevice7, optional: false);
        var vb = DirectDrawAdapters.InObject(DdConst.IidDirect3DVertexBuffer7, optional: false);

        i.Add(new GuestComMethod("Lock", 3, c => LockVertexBuffer(c, host(c.Object))));
        i.Add(new GuestComMethod("Unlock", 0, c => UnlockVertexBuffer(c, host(c.Object))));
        i.Add(new HostComMethod("ProcessVertices", 5, ComArgs.Value, ComArgs.Value, ComArgs.Value, vb, ComArgs.Value, device, ComArgs.Value).ToGuest(host));
        i.Add(new HostComMethod("GetVertexBufferDesc", 6, ComArgs.InOutStruct(DdLayouts.D3dVertexBufferDesc)).ToGuest(host));
        i.Add(new HostComMethod("Optimize", 7, device, ComArgs.Value).ToGuest(host));
        i.Absent("ProcessVerticesStrided", 7, "D3DDRAWPRIMITIVESTRIDEDDATA (12 pointer+stride pairs) has no adapter row");
        return i;
    }

    private static uint LockVertexBuffer(GuestComCall c, HostComInterface host)
    {
        uint flags = c.Arg(0), dataOut = c.Arg(1), sizeOut = c.Arg(2);
        if (dataOut == 0) return DdConst.DdErrInvalidParams;
        nint hostBits = 0;
        uint hostSize = 0;
        var hr = host.Invoke(3, (nint)flags, (nint)(&hostBits), (nint)(&hostSize));
        WindowProcBridge.ThrowPendingCallbackFault();
        if (!ComHr.Succeeded(hr)) return hr;
        try
        {
            if (hostSize == 0)
            {
                Span<uint> desc = stackalloc uint[4];
                desc[0] = DdLayouts.D3dVertexBufferDesc;
                fixed (uint* pDesc = desc)
                {
                    if (ComHr.Succeeded(host.Invoke(6, (nint)pDesc)))
                        hostSize = checked(desc[3] * (uint)FvfStride(desc[2]));
                }
            }
            if (hostBits == 0 || hostSize == 0 || hostSize > 64 * 1024 * 1024)
                throw new Win32UsageException($"IDirect3DVertexBuffer7::Lock: the host returned an unusable lock (bits 0x{hostBits:X}, size {hostSize}).");
            var buffer = c.Mem.Allocate((int)hostSize);
            c.Mem.Write(buffer.Address, new ReadOnlySpan<byte>((void*)hostBits, (int)hostSize));
            lock (vbLocks) vbLocks.GetValue(c.Object, _ => new VbLocks()).Active.Add(new VbShadow { Buffer = buffer, HostBits = hostBits, Size = (int)hostSize, Flags = flags });
            c.Mem.WriteUInt32(dataOut, buffer.Address);
            if (sizeOut != 0) c.Mem.WriteUInt32(sizeOut, hostSize);
            return hr;
        }
        catch
        {
            host.Invoke(4);
            throw;
        }
    }

    private static uint UnlockVertexBuffer(GuestComCall c, HostComInterface host)
    {
        VbShadow? shadow = null;
        lock (vbLocks)
        {
            var active = vbLocks.GetValue(c.Object, _ => new VbLocks()).Active;
            if (active.Count > 0)
            {
                shadow = active[^1];
                active.RemoveAt(active.Count - 1);
            }
        }
        if (shadow is null) return 0x887601AA;   // DDERR_NOTLOCKED
        try
        {
            if ((shadow.Flags & DdLockReadOnly) == 0)
                c.Mem.Read(shadow.Buffer.Address, new Span<byte>((void*)shadow.HostBits, shadow.Size));
        }
        finally { shadow.Buffer.Dispose(); }
        var hr = host.Invoke(4);
        WindowProcBridge.ThrowPendingCallbackFault();
        return hr;
    }
}
