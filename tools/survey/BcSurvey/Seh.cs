using System.Text;
using System.Text.RegularExpressions;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

namespace BcSurvey;

/// <summary>Part 1: which SEH-adjacent imports does a library have, where are they called, and are those sites reachable.</summary>
public static class Seh
{
    // Exact import names (any DLL) that take part in structured/C++ exception handling or probe memory under SEH.
    public static readonly string[] Names =
    [
        "RtlUnwind", "RaiseException", "RtlRaiseException", "SetUnhandledExceptionFilter", "UnhandledExceptionFilter", "_except_handler3",
        "_except_handler4_common", "__CxxFrameHandler", "__CxxFrameHandler3", "_CxxThrowException", "__CxxLongjmpUnwind", "_local_unwind2",
        "_global_unwind2", "_EH_prolog", "RtlUnwindEx", "RtlCaptureContext", "AddVectoredExceptionHandler", "RemoveVectoredExceptionHandler",
        "IsBadReadPtr", "IsBadWritePtr", "IsBadCodePtr", "IsBadStringPtrA", "IsBadStringPtrW", "IsBadHugeReadPtr", "IsBadHugeWritePtr",
        "setjmp", "_setjmp3", "longjmp", "abort", "terminate", "DebugBreak", "IsDebuggerPresent", "OutputDebugStringA",
    ];

    public static string Run(string label, string path, string[] testSources, string[] importers, StringBuilder md)
    {
        var bytes = File.ReadAllBytes(path);
        var image = PeReader.Read(bytes);
        var cfg = ControlFlowGraph.Build(image, image.MapImage());
        var v = new CfgView(image, cfg);
        md.AppendLine($"### {label}");
        md.AppendLine();
        md.AppendLine($"`{Path.GetFileName(path)}`: {PeFacts.Read(bytes, image).Kind}, {image.Imports.Count} imports, {image.Exports.Count} exports, {cfg.Instructions.Count} decoded instructions ({cfg.Instructions.Values.Count(i => i.Speculative)} speculative), {cfg.FunctionEntries.Count} function entries, relocations {image.Relocations.Count}.");
        md.AppendLine();

        // exports -> function entries; test roots = export names that occur as tokens in the test sources
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in testSources) foreach (Match m in Regex.Matches(File.ReadAllText(s), @"[A-Za-z0-9_@?$]+")) tokens.Add(m.Value);
        static string Undecorate(string n) { n = n.TrimStart('_'); var at = n.IndexOf('@'); return at > 0 ? n[..at] : n; }
        // exports another module of the exercised set imports from this one are roots too (cross-module calls)
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var imp in importers)
            foreach (var i in PeReader.Read(File.ReadAllBytes(imp)).Imports.Where(i => string.Equals(Path.GetFileNameWithoutExtension(i.Dll), label, StringComparison.OrdinalIgnoreCase) && i.Name is not null))
                tokens.Add(i.Name!);
        var exportFn = image.Exports.Where(e => e.ForwarderTarget is null && cfg.Instructions.ContainsKey(e.Rva)).Select(e => (e, f: v.FunctionOf(e.Rva))).ToList();
        var testRoots = exportFn.Where(x => x.e.Name is not null && (tokens.Contains(x.e.Name) || tokens.Contains(Undecorate(x.e.Name)))).ToList();
        var startup = new List<uint>();
        if (image.EntryPointRva != 0 && cfg.Instructions.ContainsKey(image.EntryPointRva)) startup.Add(v.FunctionOf(image.EntryPointRva));
        var allExportClosure = v.Closure(exportFn.Select(x => x.f).Concat(startup));
        var testClosure = v.Closure(testRoots.Select(x => x.f).Concat(startup));
        var startupClosure = v.Closure(startup);
        md.AppendLine($"Test-exercised export roots found in the test sources: {testRoots.Count} of {exportFn.Count} exports (+ entry point). Direct-call closure sizes (functions): all exports+entry {allExportClosure.Count}, test roots+entry {testClosure.Count}, entry only {startupClosure.Count}, of {cfg.FunctionEntries.Count} entries.");
        md.AppendLine();

        var present = image.Imports.Select((imp, k) => (imp, k)).Where(x => x.imp.Name is not null && Names.Contains(x.imp.Name)).ToList();
        md.AppendLine("| import | delay | call sites (all / non-speculative) | IAT loads | distinct caller functions | caller functions reachable from any export or entry | ... from the test-exercised roots or entry | ... from the entry point only (DllMain/CRT init) |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        var detail = new StringBuilder();
        if (present.Count == 0) md.AppendLine("| (none of the SEH-adjacent names is imported) | | | | | | | |");
        foreach (var (imp, k) in present)
        {
            var sites = v.ImportSites(k).ToList();
            var calls = sites.Where(s => s.Kind != "load").ToList();
            var loads = sites.Count(s => s.Kind == "load");
            var fns = calls.Select(s => s.Function).Distinct().ToList();
            int R(HashSet<uint> c) => fns.Count(f => c.Contains(f));
            md.AppendLine($"| {imp.Dll}!{imp.Name} | {(imp.IsDelay ? "yes" : "no")} | {calls.Count} / {calls.Count(s => !s.Speculative)} | {loads} | {fns.Count} | {R(allExportClosure)} fn | {R(testClosure)} fn | {R(startupClosure)} fn |");
            foreach (var f in fns)
            {
                var by = exportFn.Where(x => v.Closure([x.f]).Contains(f)).Select(x => x.e.Name ?? "#" + x.e.Ordinal).Take(6).ToList();
                var cnt = calls.Count(s => s.Function == f);
                detail.AppendLine($"- `{imp.Name}` in function RVA 0x{f:X} (site RVA {string.Join("/", calls.Where(s => s.Function == f).Select(s => "0x" + s.Rva.ToString("X")))}, {cnt} site(s), {(calls.Where(s => s.Function == f).All(s => s.Speculative) ? "speculative" : "non-speculative")}); reached from exports: {(by.Count == 0 ? "none by direct calls" : string.Join(", ", by))}; in test closure: {testClosure.Contains(f)}; in startup closure: {startupClosure.Contains(f)}");
            }
        }
        md.AppendLine();
        if (detail.Length > 0) { md.AppendLine("Call-site functions:"); md.AppendLine(); md.Append(detail); md.AppendLine(); }
        var imports = image.Imports.Where(i => i.Name is not null && Names.Contains(i.Name)).Select(i => i.Name!).ToHashSet();
        md.AppendLine($"Indirect call/jump sites (register or memory target other than an import slot) in the test closure, which direct-call reachability cannot follow: {CountIndirect(v, testClosure)}.");
        md.AppendLine();

        // MSVC C++ EH signatures: 0xE06D7363 ('msc' exception code) lives in _CxxThrowException; 0x19930520 is the EH magic of the frame handler/throw info
        var throwFns = new HashSet<uint>(); var magicFns = new HashSet<uint>();
        foreach (var g in v.Insts)
            for (var o = 0; o < g.Instr.OpCount; o++)
                if (g.Instr.GetOpKind(o) is OpKind.Immediate32 or OpKind.Immediate32to64)
                {
                    if (g.Instr.GetImmediate(o) == 0xE06D7363) throwFns.Add(v.FunctionOf(g.Rva));
                    else if (g.Instr.GetImmediate(o) == 0x19930520) magicFns.Add(v.FunctionOf(g.Rva));
                }
        var throwCalls = v.Insts.Where(g => g.Instr.FlowControl == FlowControl.Call && g.Instr.Op0Kind == OpKind.NearBranch32 && throwFns.Contains(g.Instr.NearBranch32 - image.ImageBase)).ToList();
        md.AppendLine($"C++ exception signatures: functions loading 0xE06D7363 (_CxxThrowException-like): {throwFns.Count}; functions loading the EH magic 0x19930520: {magicFns.Count}; direct call sites of the throw function(s) (approximate `throw` statements): {throwCalls.Count} ({throwCalls.Count(g => !g.Speculative)} in non-speculative code, {throwCalls.Count(g => !g.Speculative && testClosure.Contains(v.FunctionOf(g.Rva)))} of those in functions reachable from the test roots/entry).");
        md.AppendLine();
        Chain(v, image, md, allExportClosure, testClosure, startupClosure);
        return string.Join(",", imports);
    }

    static int CountIndirect(CfgView v, HashSet<uint> closure)
    {
        var n = 0;
        foreach (var g in v.Insts)
            if (g.Instr.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch && closure.Contains(v.FunctionOf(g.Rva)) && v.IatSlotOf(g.Instr, true) is null) n++;
        return n;
    }

    /// <summary>fs:[0] accesses: chain installs, restores, handler immediates, C++ handler stubs.</summary>
    static void Chain(CfgView v, PeImage image, StringBuilder md, HashSet<uint> any, HashSet<uint> test, HashSet<uint> startup)
    {
        var fsReads = new List<int>(); var fsWrites = new List<int>(); int pushFs = 0, popFs = 0;
        var other = new Dictionary<string, int>(); var speculativeFs = 0;
        for (var ix = 0; ix < v.Insts.Length; ix++)
        {
            var i = v.Insts[ix].Instr;
            if (i.SegmentPrefix != Register.FS && !(i.OpCount > 0 && Enumerable.Range(0, i.OpCount).Any(o => i.GetOpKind(o) == OpKind.Memory && i.MemorySegment == Register.FS))) continue;
            if (v.Insts[ix].Speculative) { speculativeFs++; continue; }
            if (i.MemoryBase != Register.None || i.MemoryIndex != Register.None || i.MemoryDisplacement32 != 0) { var key = $"fs:[{(i.MemoryBase != Register.None ? "reg" : "0x" + i.MemoryDisplacement32.ToString("X"))}]"; other[key] = other.GetValueOrDefault(key) + 1; continue; }
            if (i.Code == Code.Push_rm32) { pushFs++; fsReads.Add(ix); }
            else if (i.Code == Code.Pop_rm32) { popFs++; fsWrites.Add(ix); }
            else if (i.Code is Code.Mov_r32_rm32 or Code.Mov_EAX_moffs32) fsReads.Add(ix);
            else if (i.Code is Code.Mov_rm32_r32 or Code.Mov_rm32_imm32 or Code.Mov_moffs32_EAX) fsWrites.Add(ix);
            else other[i.Code.ToString()] = other.GetValueOrDefault(i.Code.ToString()) + 1;
        }
        var writeFns = fsWrites.Select(ix => v.FunctionOf(v.Insts[ix].Rva)).Distinct().ToList();
        // handler immediate within the 10 instructions before a write (same function)
        uint? HandlerBefore(int ix, uint fn)
        {
            for (var k = ix - 1; k >= Math.Max(0, ix - 10) && v.FunctionOf(v.Insts[k].Rva) == fn; k--)
            {
                var i = v.Insts[k].Instr;
                if (i.Code is Code.Pushd_imm32 && v.InExec(i.Immediate32)) return i.Immediate32 - image.ImageBase;
                if (i.Code is Code.Mov_r32_imm32 && v.InExec(i.Immediate32)) return i.Immediate32 - image.ImageBase;
            }
            return null;
        }
        var direct = new Dictionary<uint, HashSet<uint>>(); // function -> handlers installed inline
        foreach (var ix in fsWrites)
        {
            var fn = v.FunctionOf(v.Insts[ix].Rva);
            if (HandlerBefore(ix, fn) is { } h) { if (!direct.TryGetValue(fn, out var set)) direct[fn] = set = []; set.Add(h); }
        }
        var readFns = fsReads.Select(ix => v.FunctionOf(v.Insts[ix].Rva)).ToHashSet();
        var helpers = writeFns.Where(f => !direct.ContainsKey(f) && readFns.Contains(f)).ToList();   // reads AND writes fs:[0] with no inline handler: __SEH_prolog/__EH_prolog style helper
        var restoreOnly = writeFns.Count(f => !direct.ContainsKey(f) && !readFns.Contains(f));      // writes only: epilog helpers (__SEH_epilog) or inline restores
        var users = new Dictionary<uint, HashSet<uint>>();                           // helper -> caller function -> handler
        var helperHandlers = new Dictionary<uint, int>();
        var helperCallers = new HashSet<uint>();
        foreach (var g in v.Insts)
        {
            var i = g.Instr;
            if (i.FlowControl != FlowControl.Call || i.Op0Kind != OpKind.NearBranch32) continue;
            var t = i.NearBranch32 - image.ImageBase;
            if (!helpers.Contains(t) || v.FunctionOf(g.Rva) == t) continue;
            var caller = v.FunctionOf(g.Rva);
            helperCallers.Add(caller);
        }
        var handlers = new Dictionary<uint, int>();
        foreach (var (_, set) in direct) foreach (var h in set) handlers[h] = handlers.GetValueOrDefault(h) + 1;
        int CxxStub(uint h)
        {
            var ix = v.IndexOf(h);
            if (ix < 0 || ix + 1 >= v.Insts.Length) return 0;
            return v.Insts[ix].Instr.Code == Code.Mov_r32_imm32 && v.Insts[ix + 1].Instr.Code is Code.Jmp_rel32_32 or Code.Jmp_rm32 ? 1 : 0;
        }
        var cxx = handlers.Keys.Count(h => CxxStub(h) == 1);
        var inlineFns = direct.Count; var viaHelper = helperCallers.Count(c => !direct.ContainsKey(c));
        md.AppendLine("fs:[0] (exception registration chain) usage:");
        md.AppendLine();
        md.AppendLine("| item | count |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| instructions reading fs:[0] (push / mov r,fs:[0]) | {fsReads.Count} (push {pushFs}) |");
        md.AppendLine($"| instructions writing fs:[0] (mov fs:[0],r / pop) | {fsWrites.Count} (pop {popFs}) |");
        md.AppendLine($"| functions with an inline chain install (handler immediate before the write) | {inlineFns} |");
        md.AppendLine($"| prolog helper functions (read and write fs:[0], no inline handler) | {helpers.Count} |");
        md.AppendLine($"| write-only fs:[0] functions (epilog helpers / restores) | {restoreOnly} |");
        md.AppendLine($"| functions that call a prolog helper (__SEH_prolog / __EH_prolog style) | {helperCallers.Count} ({viaHelper} not also inline) |");
        md.AppendLine($"| distinct handler addresses pushed inline | {handlers.Count} (of them C++ handler stubs `mov eax,imm; jmp`: {cxx}) |");
        md.AppendLine($"| fs: accesses in speculative code (not counted above) | {speculativeFs} |");
        md.AppendLine($"| fs: accesses at other offsets or via registers (non-speculative) | {(other.Count == 0 ? "0" : string.Join(", ", other.Select(kv => $"{kv.Key} x{kv.Value}")))} |");
        md.AppendLine();
        var installers = direct.Keys.Concat(helperCallers.Where(c => !direct.ContainsKey(c))).Distinct().OrderBy(x => x).ToList();
        if (installers.Count > 0)
        {
            md.AppendLine($"Chain-installing functions ({installers.Count}): reachable from any export/entry {installers.Count(any.Contains)}, from test roots/entry {installers.Count(test.Contains)}, from the entry point only {installers.Count(startup.Contains)}. RVAs (a = any export/entry, t = test roots, s = startup only):");
            md.AppendLine();
            md.AppendLine(string.Join(", ", installers.Select(f => $"0x{f:X}[{(any.Contains(f) ? "a" : "")}{(test.Contains(f) ? "t" : "")}{(startup.Contains(f) ? "s" : "")}]")));
            md.AppendLine();
        }
        if (handlers.Count > 0)
            md.AppendLine("Top inline handler addresses by number of installing functions: " + string.Join(", ", handlers.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"0x{kv.Key:X} x{kv.Value}")) + ".");
        md.AppendLine();
    }
}
