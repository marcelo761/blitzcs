using Vortice.DXGI;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Dedicated video memory reported by the DXGI adapter that owns a renderer's device, in bytes.</summary>
public readonly record struct D3D11VideoMemory(ulong Total, ulong Available)
{
    /// <summary>Derives totals from adapter facts: the free amount is the OS budget minus current usage, never negative.</summary>
    public static D3D11VideoMemory FromAdapterValues(ulong dedicatedVideoMemory, ulong budget, ulong currentUsage) =>
        new(dedicatedVideoMemory != 0 ? dedicatedVideoMemory : budget, budget > currentUsage ? budget - currentUsage : 0);

    /// <summary>Converts a byte count to the signed 32-bit value exposed to BASIC, saturating instead of wrapping.</summary>
    public static int ToBasicInteger(ulong bytes) => bytes > int.MaxValue ? int.MaxValue : (int)bytes;
}

public sealed unsafe partial class D3D11Renderer
{
    /// <summary>Queries the real DXGI adapter; throws when the adapter cannot report video memory accounting.</summary>
    public D3D11VideoMemory QueryVideoMemory()
    {
        CheckAccess();
        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>()
            ?? throw new PlatformNotSupportedException("The graphics adapter does not expose DXGI video memory accounting (IDXGIAdapter3).");
        var description = adapter.Description;
        var local = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
        return D3D11VideoMemory.FromAdapterValues((ulong)description.DedicatedVideoMemory, local.Budget, local.CurrentUsage);
    }
}
