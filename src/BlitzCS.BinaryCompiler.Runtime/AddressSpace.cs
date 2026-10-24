namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Reads /proc/self/maps to name what occupies an address range (diagnostics only; nothing is ever unmapped or overwritten).</summary>
internal static class AddressSpace
{
    internal static string DescribeConflicts(uint start, ulong length)
    {
        try
        {
            var end = start + length;
            var hits = new List<string>();
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                var range = parts[0].Split('-');
                if (range.Length != 2) continue;
                var lo = Convert.ToUInt64(range[0], 16); var hi = Convert.ToUInt64(range[1], 16);
                if (lo >= end || hi <= start) continue;
                hits.Add($"0x{lo:X}-0x{hi:X} {(parts.Length > 1 ? parts[1] : "")} {(parts.Length > 5 ? parts[5].Trim() : "[anonymous]")}".TrimEnd());
                if (hits.Count == 4) { hits.Add("..."); break; }
            }
            return hits.Count == 0 ? "an unknown mapping (none visible in /proc/self/maps; the kernel refused the fixed address)" : string.Join("; ", hits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return "an existing mapping (/proc/self/maps unavailable)"; }
    }
}
