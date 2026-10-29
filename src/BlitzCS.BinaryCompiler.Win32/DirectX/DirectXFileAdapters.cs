using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BlitzCS.BinaryCompiler.Win32.Com;
using BlitzCS.BinaryCompiler.Win32.HostBridge;

namespace BlitzCS.BinaryCompiler.Win32.DirectX;

/// <summary>
/// COM adapter tables for the DirectX File API (d3dxof.dll).
/// Supports IDirectXFile, IDirectXFileEnumObject, IDirectXFileObject, IDirectXFileData,
/// IDirectXFileDataReference, IDirectXFileBinary, and IDirectXFileSaveObject.
/// Shadow guest buffers are maintained for GetData, GetType, and GetMimeType so that 32-bit guest
/// code receives valid guest-space pointers.
/// </summary>
public static unsafe class DirectXFileAdapters
{
    public static readonly Guid IidDirectXFile = new("3D82AB40-62DA-11CF-AB39-0020AF71E433");
    public static readonly Guid IidDirectXFileEnumObject = new("3D82AB41-62DA-11CF-AB39-0020AF71E433");
    public static readonly Guid IidDirectXFileSaveObject = new("3D82AB42-62DA-11CF-AB39-0020AF71E433");
    public static readonly Guid IidDirectXFileObject = new("3D82AB43-62DA-11CF-AB39-0020AF71E433");
    public static readonly Guid IidDirectXFileData = new("3D82AB44-62DA-11CF-AB39-0020AF71E433");
    public static readonly Guid IidDirectXFileDataReference = new("3D82AB45-62DA-11CF-AB39-0020AF71E433");
    public static readonly Guid IidDirectXFileBinary = new("3D82AB46-62DA-11CF-AB39-0020AF71E433");

    private static GuestComInterface? file, enumObj, saveObj, obj, data, dataRef, binary;

    public static GuestComInterface? Table(Guid iid) =>
        iid == IidDirectXFile ? file ??= DirectXFile() :
        iid == IidDirectXFileEnumObject ? enumObj ??= DirectXFileEnumObject() :
        iid == IidDirectXFileData ? data ??= DirectXFileData() :
        iid == IidDirectXFileObject ? obj ??= DirectXFileObject() :
        iid == IidDirectXFileDataReference ? dataRef ??= DirectXFileDataReference() :
        iid == IidDirectXFileBinary ? binary ??= DirectXFileBinary() :
        iid == IidDirectXFileSaveObject ? saveObj ??= DirectXFileSaveObject() :
        null;

    private static Func<GuestComObject, HostComInterface> HostOf(Guid iid) => o => ((HostComProxy)o.State!).Host(iid);
    private static ComArg OutObject(Guid iid) => ComArgs.OutInterface((s, native) => HostComRegistry.For(s.Call.Host, DirectXTables.Find).Wrap(native, iid));
    private static ComArg InObject(Guid iid, bool optional = false) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("An interface pointer must not be NULL.");
        var obj = s.Call.Host.Find(v) ?? throw new ComArgumentException($"0x{v:X8} is not a COM object of this process.");
        return obj.State is HostComProxy p && p.Has(iid) ? p.Host(iid).Pointer : throw new ComArgumentException($"{obj} has no native {iid} interface.");
    });

    private sealed class FileDataState
    {
        public readonly List<IWin32Allocation> Allocations = [];
        public void Clean()
        {
            lock (Allocations)
            {
                foreach (var a in Allocations) a.Dispose();
                Allocations.Clear();
            }
        }
        ~FileDataState() => Clean();
    }
    private static readonly ConditionalWeakTable<GuestComObject, FileDataState> fileDataStates = new();

    // ---- IDirectXFile ----

    private static GuestComInterface DirectXFile()
    {
        var i = new GuestComInterface("IDirectXFile", IidDirectXFile);
        var host = HostOf(IidDirectXFile);
        i.Add(new GuestComMethod("CreateEnumObject", 3, c => CreateEnumObject(c, host(c.Object))));
        i.Add(new HostComMethod("CreateSaveObject", 4, ComArgs.AnsiString(optional: false), ComArgs.Value, OutObject(IidDirectXFileSaveObject)).ToGuest(host));
        i.Add(new GuestComMethod("RegisterTemplates", 2, c => RegisterTemplates(c, host(c.Object))));
        return i;
    }

    private static uint CreateEnumObject(GuestComCall c, HostComInterface host)
    {
        uint pvSource = c.Arg(0), dwLoadOptions = c.Arg(1), ppEnumObj = c.Arg(2);
        if (ppEnumObj == 0) return ComHr.InvalidArg;
        c.Mem.WriteUInt32(ppEnumObj, 0);

        using var scope = new NativeScope();
        nint nativeSource = 0;
        if (dwLoadOptions == 0 /* DXFILELOAD_FROMFILE */)
        {
            if (pvSource == 0) return ComHr.InvalidArg;
            var path = c.Mem.ReadAnsiString(pvSource);
            var hostPath = c.Ctx.Files.Translate(path).Host ?? path;
            var cleanPath = hostPath.Replace('/', '\\');
            var bytes = Encoding.Default.GetBytes(cleanPath + "\0");
            nativeSource = scope.Copy(bytes);
        }
        else if (dwLoadOptions == 2 /* DXFILELOAD_FROMMEMORY */)
        {
            if (pvSource == 0) return ComHr.InvalidArg;
            uint memPtr = c.Mem.ReadUInt32(pvSource);
            uint size = c.Mem.ReadUInt32(pvSource + 4);
            var bytes = c.Mem.ReadBytes(memPtr, (int)size);
            var nativeMem = scope.Copy(bytes);
            var hostStruct = scope.AllocZeroed(16);
            *(nint*)hostStruct = nativeMem;
            *(uint*)(hostStruct + 8) = size;
            nativeSource = hostStruct;
        }
        else
        {
            throw new Win32UsageException($"IDirectXFile::CreateEnumObject: dwLoadOptions 0x{dwLoadOptions:X} is not supported.");
        }

        nint enumObj = 0;
        var hr = host.Invoke(3, nativeSource, (nint)dwLoadOptions, (nint)(&enumObj));
        if (!ComHr.Succeeded(hr) || enumObj == 0) return ComHr.Succeeded(hr) ? ComHr.Fail : hr;

        c.Mem.WriteUInt32(ppEnumObj, HostComRegistry.For(c.Host, DirectXTables.Find).Wrap(enumObj, IidDirectXFileEnumObject));
        return ComHr.Ok;
    }

    private static uint RegisterTemplates(GuestComCall c, HostComInterface host)
    {
        uint pvData = c.Arg(0), cbSize = c.Arg(1);
        if (pvData == 0 && cbSize != 0) return ComHr.InvalidArg;
        using var scope = new NativeScope();
        nint nativeData = 0;
        if (cbSize != 0)
        {
            var bytes = c.Mem.ReadBytes(pvData, (int)cbSize);
            nativeData = scope.Copy(bytes);
        }
        return host.Invoke(5, nativeData, (nint)cbSize);
    }

    // ---- IDirectXFileEnumObject ----

    private static GuestComInterface DirectXFileEnumObject()
    {
        var i = new GuestComInterface("IDirectXFileEnumObject", IidDirectXFileEnumObject);
        var host = HostOf(IidDirectXFileEnumObject);
        i.Add(new HostComMethod("GetNextDataObject", 3, OutObject(IidDirectXFileData)).ToGuest(host));
        i.Add(new HostComMethod("GetDataObjectById", 4, ComArgs.Guid(optional: false), OutObject(IidDirectXFileData)).ToGuest(host));
        i.Add(new HostComMethod("GetDataObjectByName", 5, ComArgs.AnsiString(optional: false), OutObject(IidDirectXFileData)).ToGuest(host));
        return i;
    }

    // ---- IDirectXFileObject ----

    private static GuestComInterface DirectXFileObject()
    {
        var i = new GuestComInterface("IDirectXFileObject", IidDirectXFileObject);
        var host = HostOf(IidDirectXFileObject);
        i.Add(new GuestComMethod("GetName", 2, c => GetName(c, host(c.Object))));
        i.Add(new HostComMethod("GetId", 4, ComArgs.OutBytes(16)).ToGuest(host));
        return i;
    }

    private static uint GetName(GuestComCall c, HostComInterface host)
    {
        uint nameBuf = c.Arg(0), bufLen = c.Arg(1);
        if (bufLen == 0) return ComHr.InvalidArg;
        uint len = c.Mem.ReadUInt32(bufLen);
        using var scope = new NativeScope();
        nint nativeBuf = (nameBuf != 0 && len > 0) ? scope.AllocZeroed((int)len) : 0;
        uint nativeLen = len;
        var hr = host.Invoke(3, nativeBuf, (nint)(&nativeLen));
        if (ComHr.Succeeded(hr))
        {
            c.Mem.WriteUInt32(bufLen, nativeLen);
            if (nameBuf != 0 && nativeBuf != 0 && nativeLen > 0)
            {
                var bytes = new ReadOnlySpan<byte>((void*)nativeBuf, (int)Math.Min(len, nativeLen));
                c.Mem.Write(nameBuf, bytes);
            }
        }
        return hr;
    }

    // ---- IDirectXFileData ----

    private static GuestComInterface DirectXFileData()
    {
        var i = new GuestComInterface("IDirectXFileData", IidDirectXFileData);
        i.Answers(IidDirectXFileObject);
        var host = HostOf(IidDirectXFileData);
        i.Add(new GuestComMethod("GetName", 2, c => GetName(c, host(c.Object))));
        i.Add(new HostComMethod("GetId", 4, ComArgs.OutBytes(16)).ToGuest(host));
        i.Add(new GuestComMethod("GetData", 3, c => GetData(c, host(c.Object))));
        i.Add(new GuestComMethod("GetType", 1, c => GetType(c, host(c.Object))));
        i.Add(new HostComMethod("GetNextObject", 7, OutObject(IidDirectXFileObject)).ToGuest(host));
        i.Add(new HostComMethod("AddDataObject", 8, InObject(IidDirectXFileData)).ToGuest(host));
        i.Add(new HostComMethod("AddDataReference", 9, ComArgs.AnsiString(optional: false), ComArgs.Guid(optional: true)).ToGuest(host));
        i.Add(new GuestComMethod("AddBinaryObject", 5, c => AddBinaryObject(c, host(c.Object))));
        return i;
    }

    private static uint GetData(GuestComCall c, HostComInterface host)
    {
        uint szMember = c.Arg(0), pcbSize = c.Arg(1), ppvData = c.Arg(2);
        if (pcbSize == 0 || ppvData == 0) return ComHr.InvalidArg;

        using var scope = new NativeScope();
        nint nativeMember = 0;
        if (szMember != 0)
        {
            var memberStr = c.Mem.ReadAnsiString(szMember);
            nativeMember = scope.Copy(Encoding.Default.GetBytes(memberStr + "\0"));
        }

        uint hostSize = 0;
        nint hostData = 0;
        var hr = host.Invoke(5, nativeMember, (nint)(&hostSize), (nint)(&hostData));
        if (!ComHr.Succeeded(hr)) return hr;

        c.Mem.WriteUInt32(pcbSize, hostSize);
        if (hostData == 0 || hostSize == 0)
        {
            c.Mem.WriteUInt32(ppvData, 0);
            return hr;
        }

        var state = fileDataStates.GetOrCreateValue(c.Object);
        var alloc = c.Mem.Allocate((int)hostSize);
        lock (state.Allocations) state.Allocations.Add(alloc);
        c.Mem.Write(alloc.Address, new ReadOnlySpan<byte>((void*)hostData, (int)hostSize));
        c.Mem.WriteUInt32(ppvData, alloc.Address);
        return hr;
    }

    private static uint GetType(GuestComCall c, HostComInterface host)
    {
        uint pptype = c.Arg(0);
        if (pptype == 0) return ComHr.InvalidArg;

        nint hostGuidPtr = 0;
        var hr = host.Invoke(6, (nint)(&hostGuidPtr));
        if (!ComHr.Succeeded(hr)) return hr;

        if (hostGuidPtr == 0)
        {
            c.Mem.WriteUInt32(pptype, 0);
            return hr;
        }

        var state = fileDataStates.GetOrCreateValue(c.Object);
        var alloc = c.Mem.Allocate(16);
        lock (state.Allocations) state.Allocations.Add(alloc);
        c.Mem.Write(alloc.Address, new ReadOnlySpan<byte>((void*)hostGuidPtr, 16));
        c.Mem.WriteUInt32(pptype, alloc.Address);
        return hr;
    }

    private static uint AddBinaryObject(GuestComCall c, HostComInterface host)
    {
        uint szName = c.Arg(0), pguid = c.Arg(1), szMimeType = c.Arg(2), pvData = c.Arg(3), cbSize = c.Arg(4);
        using var scope = new NativeScope();
        nint nativeName = szName != 0 ? scope.Copy(Encoding.Default.GetBytes(c.Mem.ReadAnsiString(szName) + "\0")) : 0;
        nint nativeGuid = pguid != 0 ? scope.Copy(c.Mem.ReadBytes(pguid, 16)) : 0;
        nint nativeMime = szMimeType != 0 ? scope.Copy(Encoding.Default.GetBytes(c.Mem.ReadAnsiString(szMimeType) + "\0")) : 0;
        nint nativeData = (pvData != 0 && cbSize != 0) ? scope.Copy(c.Mem.ReadBytes(pvData, (int)cbSize)) : 0;
        return host.Invoke(10, nativeName, nativeGuid, nativeMime, nativeData, (nint)cbSize);
    }

    // ---- IDirectXFileDataReference ----

    private static GuestComInterface DirectXFileDataReference()
    {
        var i = new GuestComInterface("IDirectXFileDataReference", IidDirectXFileDataReference);
        i.Answers(IidDirectXFileObject);
        var host = HostOf(IidDirectXFileDataReference);
        i.Add(new GuestComMethod("GetName", 2, c => GetName(c, host(c.Object))));
        i.Add(new HostComMethod("GetId", 4, ComArgs.OutBytes(16)).ToGuest(host));
        i.Add(new HostComMethod("Resolve", 5, OutObject(IidDirectXFileData)).ToGuest(host));
        return i;
    }

    // ---- IDirectXFileBinary ----

    private static GuestComInterface DirectXFileBinary()
    {
        var i = new GuestComInterface("IDirectXFileBinary", IidDirectXFileBinary);
        i.Answers(IidDirectXFileObject);
        var host = HostOf(IidDirectXFileBinary);
        i.Add(new GuestComMethod("GetName", 2, c => GetName(c, host(c.Object))));
        i.Add(new HostComMethod("GetId", 4, ComArgs.OutBytes(16)).ToGuest(host));
        i.Add(new HostComMethod("GetSize", 5, ComArgs.OutU32()).ToGuest(host));
        i.Add(new GuestComMethod("GetMimeType", 1, c => GetMimeType(c, host(c.Object))));
        i.Add(new GuestComMethod("Read", 3, c => ReadBinary(c, host(c.Object))));
        return i;
    }

    private static uint GetMimeType(GuestComCall c, HostComInterface host)
    {
        uint pszMimeType = c.Arg(0);
        if (pszMimeType == 0) return ComHr.InvalidArg;
        nint hostMimePtr = 0;
        var hr = host.Invoke(6, (nint)(&hostMimePtr));
        if (!ComHr.Succeeded(hr)) return hr;
        if (hostMimePtr == 0)
        {
            c.Mem.WriteUInt32(pszMimeType, 0);
            return hr;
        }
        var mimeStr = Marshal.PtrToStringAnsi(hostMimePtr) ?? "";
        var bytes = Encoding.Default.GetBytes(mimeStr + "\0");
        var state = fileDataStates.GetOrCreateValue(c.Object);
        var alloc = c.Mem.Allocate(bytes.Length);
        lock (state.Allocations) state.Allocations.Add(alloc);
        c.Mem.Write(alloc.Address, bytes);
        c.Mem.WriteUInt32(pszMimeType, alloc.Address);
        return hr;
    }

    private static uint ReadBinary(GuestComCall c, HostComInterface host)
    {
        uint pvData = c.Arg(0), cbSize = c.Arg(1), pcbRead = c.Arg(2);
        if (pvData == 0 && cbSize > 0) return ComHr.InvalidArg;
        using var scope = new NativeScope();
        nint nativeBuf = cbSize > 0 ? scope.AllocZeroed((int)cbSize) : 0;
        uint nativeRead = 0;
        var hr = host.Invoke(7, nativeBuf, (nint)cbSize, (nint)(&nativeRead));
        if (ComHr.Succeeded(hr))
        {
            if (pcbRead != 0) c.Mem.WriteUInt32(pcbRead, nativeRead);
            if (nativeBuf != 0 && nativeRead > 0)
            {
                var bytes = new ReadOnlySpan<byte>((void*)nativeBuf, (int)Math.Min(cbSize, nativeRead));
                c.Mem.Write(pvData, bytes);
            }
        }
        return hr;
    }

    // ---- IDirectXFileSaveObject ----

    private static GuestComInterface DirectXFileSaveObject()
    {
        var i = new GuestComInterface("IDirectXFileSaveObject", IidDirectXFileSaveObject);
        i.Absent("SaveTemplates", 2, "saving templates is not supported");
        i.Absent("CreateDataObject", 6, "saving data objects is not supported");
        i.Absent("SaveData", 1, "saving data is not supported");
        return i;
    }
}
