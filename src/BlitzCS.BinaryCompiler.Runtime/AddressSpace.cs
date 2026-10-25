namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Names what occupies an address range through <see cref="IPlatformMemory.QueryRanges"/> (/proc/self/maps or VirtualQuery); diagnostics only: nothing is ever unmapped or overwritten.</summary>
internal static class AddressSpace
{
    internal static string DescribeConflicts(uint start, ulong length)
    {
        var platform = PlatformMemory.Current;
        var source = platform.Name == "windows" ? "VirtualQuery" : "/proc/self/maps";
        try
        {
            var hits = new List<string>();
            foreach (var range in platform.QueryRanges(start, start + length))
            {
                hits.Add($"0x{range.Start:X}-0x{range.End:X} {range.Description}".TrimEnd());
                if (hits.Count == 4) { hits.Add("..."); break; }
            }
            return hits.Count == 0 ? $"an unknown mapping (none visible in {source}; the system refused the fixed address)" : string.Join("; ", hits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return $"an existing mapping ({source} unavailable)"; }
    }
}
