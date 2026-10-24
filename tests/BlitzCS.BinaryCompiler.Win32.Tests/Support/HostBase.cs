using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// Places a real library (linked at 0x10000000) at a base the host can map. Linux: unchanged. Windows x64 cannot reserve below 0x20000000 and the process' own
/// heaps fill the bottom of the image window, so the library is rebased (through its relocations) to a free, 64 KiB aligned run in the upper part of the window.
/// </summary>
internal static class HostBase
{
    public static (PeImage Image, uint? LoadBase) Fit(PeImage image)
    {
        if (!OperatingSystem.IsWindows() || image.ImageBase >= PlatformMemory.Current.MinimumImageBase) return (image, null);
        var size = ModuleBaseAllocator.Footprint(image);
        var taken = new List<MappedRange>();
        for (ulong cursor = 0x50000000; cursor < 0x70000000;)
        {
            var part = PlatformMemory.Current.QueryRanges(cursor, 0x70000000);
            if (part.Count == 0) break;
            taken.AddRange(part);
            var next = part.Max(r => r.End);
            if (next <= cursor) break;
            cursor = next;
        }
        for (var b = 0x50000000UL; b + size <= 0x70000000; b += 0x10000)
            if (!taken.Any(r => r.Start < b + size && r.End > b)) return (image.Rebase((uint)b), (uint)b);
        throw new InvalidOperationException("no free run in the Windows image window");
    }
}
