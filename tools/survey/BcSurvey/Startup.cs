using System.Text;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Lifting;
using BlitzCS.BinaryCompiler.Win32;
using Iced.Intel;

namespace BcSurvey;

/// <summary>
/// Static start-up closure of one image: the functions reachable by direct calls and tail jumps from the entry point
/// (EXE) or DllMain (DLL) and from each named export (or the export given by ordinal). Lists the imports whose call sites
/// lie in that closure, with the Win32 component's verdict. Nothing is translated for execution and nothing runs.
/// Limit: indirect calls (vtables, function pointers, COM methods) are not followed, so COM/DirectX work reached only through
/// interface pointers is outside the closure; its imports are still reported by the sdk mode.
/// </summary>
public static class Startup
{
    public static void Run(string path, IReadOnlyList<string> exportNames, StringBuilder sb)
    {
        var bytes = File.ReadAllBytes(path);
        var image = PeReader.Read(bytes);
        var mapped = image.MapImage();
        var cfg = ControlFlowGraph.Build(image, mapped);
        var v = new CfgView(image, cfg);
        using var resolver = new Win32ImportResolver();
        var verdicts = resolver.Analyze(image);

        var roots = new List<(string Label, uint Rva)>();
        if (image.EntryPointRva != 0) roots.Add((image.IsDll ? "DllMain" : "entry", image.EntryPointRva));
        foreach (var name in exportNames)
        {
            var ex = image.Exports.FirstOrDefault(e => e.Name == name || (name.StartsWith("#") && e.Ordinal.ToString() == name[1..]));
            if (ex is null || ex.ForwarderTarget is not null) { sb.AppendLine($"# export '{name}' not found or forwarded in {Path.GetFileName(path)}"); continue; }
            roots.Add((ex.Name ?? $"#{ex.Ordinal}", ex.Rva));
        }

        sb.AppendLine($"# startup closure of {Path.GetFileName(path)} (base {image.ImageBase:X8}, direct calls and tail jumps only)");
        // per import: which roots reach a call site of it
        // address-taken functions: entries whose absolute address is stored in the image (vtables, function tables, callbacks).
        // Adding them to a root gives an upper bound of the start-up set: an indirect call may reach any of them.
        var entries = v.Entries.ToHashSet();
        var addrTaken = new HashSet<uint>();
        foreach (var r in image.Relocations)
        {
            if (r.Type != 3 || r.Rva + 4 > mapped.Length) continue;
            var target = BitConverter.ToUInt32(mapped, (int)r.Rva) - image.ImageBase;
            if (entries.Contains(target)) addrTaken.Add(v.FunctionOf(target));
        }
        sb.AppendLine($"# address-taken functions (upper-bound additions): {addrTaken.Count}");
        var reach = new Dictionary<int, SortedSet<string>>();
        var direct = new Dictionary<int, int>();
        var upper = new Dictionary<int, int>();
        var sites = v.AllImportSites();
        foreach (var (label, rva) in roots)
        {
            var closure = v.Closure([v.FunctionOf(rva)]);
            var wide = v.Closure([v.FunctionOf(rva), .. addrTaken]);
            var indirect = v.Insts.Count(g => g.Instr.FlowControl == FlowControl.IndirectCall && closure.Contains(v.FunctionOf(g.Rva)));
            sb.AppendLine($"# root {label} RVA {rva:X} -> {closure.Count} functions direct, {wide.Count} with address-taken functions, {indirect} indirect call sites inside the direct closure (targets not followed)");
            foreach (var (imp, list) in sites)
            {
                var hit = list.Count(s => s.Kind != "load" && closure.Contains(s.Function));
                var hitWide = list.Count(s => s.Kind != "load" && wide.Contains(s.Function));
                if (hitWide == 0) continue;
                if (!reach.TryGetValue(imp, out var set)) reach[imp] = set = [];
                set.Add(label);
                direct[imp] = direct.GetValueOrDefault(imp) + hit;
                upper[imp] = upper.GetValueOrDefault(imp) + hitWide;
            }
        }
        sb.AppendLine();
        sb.AppendLine("dll\tname\tstatus\tservice\tdirect_sites\tupper_sites\troots");
        var rows = new List<string>();
        for (var k = 0; k < image.Imports.Count; k++)
        {
            if (!reach.TryGetValue(k, out var set)) continue;
            var imp = image.Imports[k];
            var verdict = verdicts[k];
            var name = imp.Name ?? $"#{imp.Ordinal}";
            rows.Add($"{imp.Dll}\t{name}\t{(verdict.Status == Win32ImportStatus.Implemented ? "implemented" : "MISSING")}\t{verdict.Service ?? "-"}\t{direct.GetValueOrDefault(k)}\t{upper[k]}\t{string.Join(",", set)}");
        }
        rows.Sort(StringComparer.Ordinal);
        foreach (var r in rows) sb.AppendLine(r);
    }
}
