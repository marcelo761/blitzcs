using BlitzCS.BinaryCompiler.Win32.Com;

namespace BlitzCS.BinaryCompiler.Win32.DirectX;

/// <summary>Every adapter table of the DirectX bridge, by IID. One <see cref="HostComRegistry"/> per COM host uses it, so an object of any of these interfaces keeps one guest identity.</summary>
public static class DirectXTables
{
    public static GuestComInterface? Find(Guid iid) => DirectDrawAdapters.Table(iid) ?? DirectInputAdapters.Table(iid);
}
