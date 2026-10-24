using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Lifting;
using BlitzCS.BinaryCompiler.Win32;
using Iced.Intel;

namespace BcSurvey;

public sealed class FileResult
{
    public string OsVersion = "", Flags = "", Path = "", Name = "", Kind = "", Subsystem = "", Verdict = "", Error = "", LoadBase = "", Packed = "";
    public long Size; public uint PreferredBase, Entry, SizeOfImage; public int Imports, DelayImports, Exports, Relocs, Instructions, Speculative, Functions, CodeSize;
    public bool Tls, Clr, CannotMove; public double Ms;
    public string EntryBytes = "";
    public SortedDictionary<string, int> Errors = [], Warnings = [], Infos = [];
    public SortedDictionary<string, (int Functions, int Sites, int Implemented)> ByContract = [];
}

public sealed class UnsupportedSite { public string File = "", Section = "", Bytes = "", Reason = "", Mnemonic = ""; public uint Rva, Function; public bool Speculative; }
public sealed class ImportAgg
{
    public string Dll = "", Name = ""; public string Status = ""; public string? Service; public HashSet<string> Files = [];
    public int Sites, NonSpecSites, StartupSites;
}

public static class Sdk
{
    static readonly Regex UnsupportedRx = new(@"^Unsupported instruction '([^']+)': (.*)\.$", RegexOptions.Compiled);

    static readonly HashSet<string> K32Seh = ["UnhandledExceptionFilter", "RtlUnwind", "RaiseException", "IsDebuggerPresent", "SetUnhandledExceptionFilter", "RtlCaptureContext", "RtlLookupFunctionEntry", "RtlVirtualUnwind", "DebugBreak", "OutputDebugStringA", "OutputDebugStringW"];
    static readonly HashSet<string> K32Crt = ["EncodePointer", "DecodePointer", "IsProcessorFeaturePresent", "GetModuleHandleExW", "GetSystemTimeAsFileTime", "QueryPerformanceCounter", "InitializeSListHead", "GetCurrentProcessId", "GetTickCount64", "InitializeCriticalSectionAndSpinCount", "FlsAlloc", "FlsGetValue", "FlsSetValue", "FlsFree", "GetStartupInfoW", "GetCommandLineW", "GetEnvironmentStringsW", "FreeEnvironmentStringsW", "GetModuleFileNameW", "GetModuleHandleW", "LoadLibraryExW", "SetEnvironmentVariableW", "SetHandleCount", "GetStdHandle"];
    static readonly HashSet<string> K32Res = ["LoadResource", "LockResource", "FindResourceA", "FindResourceW", "SizeofResource", "MulDiv", "GlobalLock", "GlobalUnlock", "GlobalSize", "GlobalHandle", "GlobalReAlloc", "LocalReAlloc", "GlobalAlloc", "GlobalFree", "LocalAlloc", "LocalFree", "lstrcmpA", "lstrcpyA", "lstrlenA", "lstrcmpiA", "FreeResource"];

    /// <summary>Contract groups used in the readiness doc. kernel32 is split because its absent functions are not one thing.</summary>
    public static string Contract(string dll, string? fn)
    {
        var d = dll.ToLowerInvariant();
        if (d.EndsWith(".dll")) d = d[..^4];
        if (d is "ddraw" or "d3d8" or "d3d9" or "d3d11" or "dsound" or "dinput" or "dinput8" or "dplayx" or "dxguid" or "d3dim" or "d3drm" or "d3dxof" || d.StartsWith("d3dx") || d == "dxtrans") return "DirectX/COM creation (" + d + ")";
        if (d is "opengl32" or "glu32") return "OpenGL (" + d + ")";
        if (d is "user32") return "user32 window/message pump";
        if (d is "comctl32" or "comdlg32" or "shell32" or "shlwapi" or "imm32" or "uxtheme" or "dwmapi" or "oleacc" or "oledlg") return "shell / theme / IME / accessibility UI";
        if (d is "gdi32" or "msimg32" or "winspool.drv" or "winspool" or "gdiplus") return "gdi32 / gdiplus / printing";
        if (d is "winmm" or "msacm32" or "avifil32" or "msvfw32") return "winmm multimedia";
        if (d is "msvcrt" or "mfc42" or "mfc42u" or "msvcirt" || d.StartsWith("msvcp") || d.StartsWith("msvcr") || d.StartsWith("vcruntime") || d.StartsWith("api-ms-")) return "CRT / C++ runtime DLL";
        if (d is "ole32" or "oleaut32" or "ole" or "olepro32") return "ole32 / oleaut32 COM & automation";
        if (d is "kernel32")
        {
            var n = fn ?? "";
            if (K32Seh.Contains(n)) return "kernel32: SEH / diagnostics";
            if (K32Crt.Contains(n)) return "kernel32: MSVC 2010+ CRT startup";
            if (K32Res.Contains(n)) return "kernel32: resources & global/local memory";
            if (n.EndsWith("W") || n.Contains("Locale") || n.Contains("Console") || n.Contains("CodePage") || n.Contains("LCMap") || n.Contains("Nls")) return "kernel32: wide-char file/console/locale";
            return "kernel32: other";
        }
        if (d is "advapi32" or "version" or "psapi" or "ntdll") return "advapi32 / version / psapi / ntdll";
        if (d is "ws2_32" or "wsock32" or "wininet" or "winhttp" or "urlmon") return "network";
        return "other (" + d + ")";
    }

    static string Classify(string mnemonic, string reason, bool speculative)
    {
        var m = mnemonic.ToLowerInvariant();
        string[] privileged = ["in", "out", "insb", "insw", "insd", "outsb", "outsw", "outsd", "cli", "sti", "hlt", "rdmsr", "wrmsr", "lgdt", "lidt", "lldt", "ltr", "invd", "wbinvd", "clts", "iretd", "iret", "iretw", "sysenter", "sysexit", "syscall", "sysret", "int1", "int", "into"];
        if (reason.StartsWith("16-bit addressing") || reason.StartsWith("far or 16-bit")) return "16-bit/far form: data (or a 16-bit stub) decoded as code";
        if (m == "int 0x29") return "valid in user mode, unlowered: int 29h = __fastfail (MSVC /GS and abort paths)";
        if (m.StartsWith("int 0x2e") || m.StartsWith("int 0x2e")) return "valid in user mode, unlowered: int 2Eh = legacy system call";
        if (privileged.Contains(m.Split(' ')[0])) return "ring-0/I-O or trap opcode: data decoded as code unless deliberate (faults in user mode)";
        if (reason.StartsWith("segment register operand")) return "valid in user mode, unlowered: segment-register move/push/pop";
        if (reason.StartsWith("3DNow")) return "valid on AMD CPUs, unlowered: 3DNow!";
        if (reason.StartsWith("VEX")) return "valid, unlowered: VEX/EVEX (not IA-32 era)";
        if (reason.StartsWith("not valid in 64-bit mode")) return "valid in user mode IA-32, invalid in x64: needs a lowering (" + m + ")";
        if (reason.StartsWith("cannot be encoded")) return "valid in user mode, re-encoding failed (" + m + ")";
        return "other: " + reason;
    }

    public static void Run(string dir, StringBuilder md, string? jsonPath)
    {
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try { using var s = File.OpenRead(f); var b = new byte[2]; if (s.Read(b) == 2 && b[0] == 'M' && b[1] == 'Z') files.Add(f); } catch (IOException) { }
        }
        var allNames = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).ToHashSet();
        using var resolver = new Win32ImportResolver();
        var results = new List<FileResult>();
        var unsup = new List<UnsupportedSite>();
        var imports = new Dictionary<(string, string), ImportAgg>();
        var exeStartup = new List<(string File, List<ImportAgg> Missing, int Reached, int ReachedMissing, int Functions, int ClosureFunctions)>();
        var otherDiag = new SortedDictionary<string, int>(); var otherErrors = new List<string>();
        foreach (var path in files)
        {
            var r = new FileResult { Path = path, Name = System.IO.Path.GetRelativePath(dir, path), Size = new FileInfo(path).Length };
            results.Add(r);
            PeImage image;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); image = PeReader.Read(bytes); }
            catch (Exception e) { r.Verdict = "not translatable"; r.Error = e is PeFormatException pf ? $"{pf.Code}: {pf.Message}" : e.GetType().Name + ": " + e.Message; continue; }
            var facts = PeFacts.Read(bytes, image);
            r.Kind = facts.Kind; r.Subsystem = PeFacts.SubsystemName(facts.Subsystem); r.PreferredBase = image.ImageBase; r.Entry = image.EntryPointRva; r.SizeOfImage = image.SizeOfImage;
            r.Imports = image.Imports.Count; r.DelayImports = image.Imports.Count(i => i.IsDelay); r.Exports = image.Exports.Count; r.Relocs = image.Relocations.Count(x => x.Type == 3);
            r.Tls = image.Tls is not null; r.Clr = facts.HasClr; r.OsVersion = facts.OsVersion;
            r.Flags = string.Join(",", new[] { (facts.DllCharacteristics & 0x40) != 0 ? "dynamicbase" : null, (facts.DllCharacteristics & 0x100) != 0 ? "nx" : null, (facts.DllCharacteristics & 0x400) != 0 ? "no-seh" : null, facts.HasLoadConfig ? "loadconfig(SafeSEH/CFG)" : null, facts.HasBoundImports ? "bound-imports" : null, facts.RelocsStripped ? "relocs-stripped" : null }.Where(x => x is not null));
            try { var p = PackedImageAnalyzer.Analyze(image); r.Packed = p.Verdict.ToString() + (p.Reasons.Count > 0 ? " (" + string.Join("; ", p.Reasons) + ")" : ""); } catch (Exception e) { r.Packed = "analyzer error " + e.Message; }
            // load base as the windows-x64 target needs it
            uint? loadBase = null;
            try { loadBase = ModuleBaseAllocator.RequiredBase(r.Name, image, BinaryTargets.WindowsX64); r.LoadBase = loadBase is { } lb ? $"0x{lb:X8}" : "preferred"; }
            catch (PeRebaseException e) { r.CannotMove = true; r.LoadBase = "cannot move: " + (e.Message.Length > 90 ? e.Message[..90] : e.Message); }
            var mapped = image.MapImage();
            if (image.EntryPointRva != 0 && image.EntryPointRva < mapped.Length) r.EntryBytes = Convert.ToHexString(mapped.AsSpan((int)image.EntryPointRva, Math.Min(8, mapped.Length - (int)image.EntryPointRva)));
            TranslationArtifact art;
            var sw = Stopwatch.StartNew();
            try { art = X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: BinaryTargets.WindowsX64) { LoadBase = loadBase }, "survey"); }
            catch (Exception e) { r.Verdict = "translator exception"; r.Error = e.GetType().Name + ": " + e.Message; continue; }
            r.Ms = sw.Elapsed.TotalMilliseconds; r.CodeSize = art.CodeSize;
            foreach (var d in art.Diagnostics)
            {
                var dict = d.Severity switch { DiagnosticSeverity.Error => r.Errors, DiagnosticSeverity.Warning => r.Warnings, _ => r.Infos };
                dict[d.Code] = dict.GetValueOrDefault(d.Code) + 1;
                if (d.Code == "BC_UNSUPPORTED_INSTRUCTION")
                {
                    var m = UnsupportedRx.Match(d.Message);
                    unsup.Add(new UnsupportedSite { File = r.Name, Rva = d.GuestRva ?? 0, Speculative = d.Severity == DiagnosticSeverity.Warning, Mnemonic = m.Success ? m.Groups[1].Value : "?", Reason = m.Success ? m.Groups[2].Value : d.Message });
                }
                else if (d.Severity == DiagnosticSeverity.Error) { otherErrors.Add($"| {r.Name} | {d.Code} | 0x{d.GuestRva:X} | {d.Message.Replace('|', '/')} |"); otherDiag[$"{d.Severity}:{d.Code}"] = otherDiag.GetValueOrDefault($"{d.Severity}:{d.Code}") + 1; }
                else if (d.Severity != DiagnosticSeverity.Info) otherDiag[$"{d.Severity}:{d.Code}"] = otherDiag.GetValueOrDefault($"{d.Severity}:{d.Code}") + 1;
            }
            r.Verdict = r.Errors.Count == 0 ? "translates clean" : "translates with errors";

            // CFG on the image as it is (preferred base) for site counts and reachability
            var cfg = ControlFlowGraph.Build(image, mapped);
            var v = new CfgView(image, cfg);
            r.Instructions = cfg.Instructions.Count; r.Speculative = cfg.Instructions.Values.Count(g => g.Speculative); r.Functions = cfg.FunctionEntries.Count;
            foreach (var u in unsup.Where(u => u.File == r.Name && u.Bytes == ""))
            {
                var sec = image.Sections.FirstOrDefault(s => u.Rva >= s.VirtualAddress && u.Rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
                u.Section = sec?.Name ?? "?";
                if (cfg.Instructions.TryGetValue(u.Rva, out var gi) && u.Rva + gi.Length <= mapped.Length) u.Bytes = Convert.ToHexString(mapped.AsSpan((int)u.Rva, Math.Min(gi.Length, 10)));
                else u.Bytes = "?";
                u.Function = v.FunctionOf(u.Rva);
                if (u.Mnemonic == "int" && u.Bytes.StartsWith("CD") && u.Bytes.Length >= 4) u.Mnemonic = "int 0x" + u.Bytes[2..4];
            }
            var startup = new List<uint>();
            if (image.EntryPointRva != 0 && cfg.Instructions.ContainsKey(image.EntryPointRva)) startup.Add(v.FunctionOf(image.EntryPointRva));
            var closure = v.Closure(startup);
            var verdicts = resolver.Analyze(image);
            var sites = v.AllImportSites();
            var startupMissing = new List<ImportAgg>(); int reached = 0, reachedMissing = 0;
            for (var k = 0; k < image.Imports.Count; k++)
            {
                var imp = image.Imports[k]; var verdict = verdicts[k];
                var key = (imp.Dll.ToLowerInvariant(), imp.Name ?? "#" + imp.Ordinal);
                if (!imports.TryGetValue(key, out var agg)) imports[key] = agg = new ImportAgg { Dll = key.Item1, Name = key.Item2 };
                var local = allNames.Contains(System.IO.Path.GetFileNameWithoutExtension(imp.Dll).ToLowerInvariant());
                agg.Status = verdict.Status == Win32ImportStatus.Implemented ? "implemented" : local ? "sdk-local" : "absent";
                agg.Service = verdict.Service;
                agg.Files.Add(r.Name);
                {
                    var cname = Contract(imp.Dll, imp.Name);
                    var cur = r.ByContract.GetValueOrDefault(cname);
                    var nsites = sites.TryGetValue(k, out var sl) ? sl.Count(x => x.Kind != "load") : 0;
                    r.ByContract[cname] = agg.Status == "implemented" ? (cur.Functions, cur.Sites, cur.Implemented + 1) : (cur.Functions + 1, cur.Sites + nsites, cur.Implemented);
                }
                if (sites.TryGetValue(k, out var l))
                {
                    var calls = l.Where(s => s.Kind != "load").ToList();
                    agg.Sites += calls.Count; agg.NonSpecSites += calls.Count(s => !s.Speculative);
                    var st = calls.Count(s => closure.Contains(s.Function));
                    agg.StartupSites += st;
                    if (st > 0 && r.Kind == "exe") { reached++; if (agg.Status != "implemented") { reachedMissing++; startupMissing.Add(agg); } }
                }
            }
            if (r.Kind == "exe") exeStartup.Add((r.Name, startupMissing, reached, reachedMissing, cfg.FunctionEntries.Count, closure.Count));
        }

        Emit(md, results, unsup, imports, exeStartup, otherDiag, files.Count);
        md.AppendLine().AppendLine("## Other Error diagnostics (not BC_UNSUPPORTED_INSTRUCTION)").AppendLine().AppendLine("| file | code | RVA | message |").AppendLine("|---|---|---|---|");
        foreach (var line in otherErrors) md.AppendLine(line);
        if (jsonPath is not null)
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
            {
                files = results,
                unsupported = unsup,
                imports = imports.Values.Select(a => new { a.Dll, a.Name, a.Status, a.Service, files = a.Files.Count, a.Sites, a.NonSpecSites, a.StartupSites, contract = Contract(a.Dll, a.Name) }),
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    static void Emit(StringBuilder md, List<FileResult> results, List<UnsupportedSite> unsup, Dictionary<(string, string), ImportAgg> imports,
        List<(string File, List<ImportAgg> Missing, int Reached, int ReachedMissing, int Functions, int ClosureFunctions)> exeStartup, SortedDictionary<string, int> otherDiag, int fileCount)
    {
        md.AppendLine($"## Files ({fileCount} files start with MZ)").AppendLine();
        md.AppendLine("| file | size | kind/subsystem | min OS / subsystem version | flags | preferred base | relocs | TLS | delay imp. | imports | exports | entry RVA | packed | windows-x64 base | verdict | errors | warnings | decoded insts (spec.) | funcs | translate ms |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            if (r.Kind == "") { md.AppendLine($"| {r.Name} | {r.Size} | - | - | - | - | - | - | - | - | - | - | - | - | {r.Verdict}: {r.Error} | | | | | |"); continue; }
            md.AppendLine($"| {r.Name} | {r.Size} | {r.Kind}/{r.Subsystem}{(r.Clr ? "/CLR" : "")} | {r.OsVersion} | {r.Flags} | 0x{r.PreferredBase:X8} | {r.Relocs} | {(r.Tls ? "yes" : "no")} | {r.DelayImports} | {r.Imports} | {r.Exports} | 0x{r.Entry:X} | {r.Packed} | {r.LoadBase} | {r.Verdict}{(r.Error != "" ? ": " + r.Error : "")} | {r.Errors.Values.Sum()} | {r.Warnings.Values.Sum()} | {r.Instructions} ({r.Speculative}) | {r.Functions} | {r.Ms:F0} |");
        }
        md.AppendLine().AppendLine("Diagnostic codes per file (errors / warnings):").AppendLine();
        md.AppendLine("| file | errors by code | warnings by code |").AppendLine("|---|---|---|");
        foreach (var r in results.Where(r => r.Kind != ""))
            md.AppendLine($"| {r.Name} | {string.Join(", ", r.Errors.Select(kv => $"{kv.Key} x{kv.Value}"))} | {string.Join(", ", r.Warnings.Select(kv => $"{kv.Key} x{kv.Value}"))} |");

        md.AppendLine().AppendLine("## Unsupported instructions").AppendLine();
        md.AppendLine($"BC_UNSUPPORTED_INSTRUCTION sites: {unsup.Count} total, {unsup.Count(u => !u.Speculative)} reachable (Error), {unsup.Count(u => u.Speculative)} speculative (Warning, from pointer-scan/immediate roots). Other non-info diagnostics: {string.Join(", ", otherDiag.Select(kv => $"{kv.Key} x{kv.Value}"))}.").AppendLine();
        md.AppendLine("Ranked by reachable (blocking) sites, then total.").AppendLine().AppendLine("| rank | mnemonic | reason | class | sites | reachable | speculative | files | examples (file RVA bytes section) |").AppendLine("|---|---|---|---|---|---|---|---|---|");
        var groups = unsup.GroupBy(u => (u.Mnemonic, u.Reason)).Select(g => (g.Key, Sites: g.Count(), Reach: g.Count(x => !x.Speculative), Spec: g.Count(x => x.Speculative), Files: g.Select(x => x.File).Distinct().Count(), Ex: g.OrderBy(x => x.Speculative).Take(3).ToList())).OrderByDescending(g => g.Reach).ThenByDescending(g => g.Sites).ToList();
        var rank = 1;
        foreach (var g in groups.Take(40))
            md.AppendLine($"| {rank++} | {g.Key.Mnemonic} | {g.Key.Reason} | {Classify(g.Key.Mnemonic, g.Key.Reason, g.Reach == 0)} | {g.Sites} | {g.Reach} | {g.Spec} | {g.Files} | {string.Join("; ", g.Ex.Select(e => $"{e.File} 0x{e.Rva:X} {e.Bytes} {e.Section}"))} |");
        md.AppendLine($"\n({groups.Count} distinct mnemonic/reason groups in total.)").AppendLine();
        md.AppendLine("By class:").AppendLine().AppendLine("| class | sites | reachable | speculative |").AppendLine("|---|---|---|---|");
        foreach (var c in unsup.GroupBy(u => Classify(u.Mnemonic, u.Reason, u.Speculative)).OrderByDescending(c => c.Count()))
            md.AppendLine($"| {c.Key} | {c.Count()} | {c.Count(x => !x.Speculative)} | {c.Count(x => x.Speculative)} |");
        md.AppendLine().AppendLine("Reachable (blocking) unsupported sites grouped by containing function (approximate: function = span between recovered function entries):").AppendLine();
        md.AppendLine("| file | function RVA | sites | mnemonics |").AppendLine("|---|---|---|---|");
        foreach (var g in unsup.Where(u => !u.Speculative).GroupBy(u => (u.File, u.Function)).OrderBy(g => g.Key.File).ThenBy(g => g.Key.Function))
            md.AppendLine($"| {g.Key.File} | 0x{g.Key.Function:X} | {g.Count()} | {string.Join(", ", g.GroupBy(x => x.Mnemonic).Select(x => $"{x.Key} x{x.Count()}"))} |");
        md.AppendLine().AppendLine("Unsupported sites per file and section:").AppendLine().AppendLine("| file | section | reachable | speculative |").AppendLine("|---|---|---|---|");
        foreach (var g in unsup.GroupBy(u => (u.File, u.Section)).OrderBy(g => g.Key.File).ThenBy(g => g.Key.Section))
            md.AppendLine($"| {g.Key.File} | {g.Key.Section} | {g.Count(x => !x.Speculative)} | {g.Count(x => x.Speculative)} |");

        md.AppendLine().AppendLine("## Absent imports per file and contract").AppendLine();
        md.AppendLine("Absent functions / static call sites of those (implemented functions of the same contract in brackets).").AppendLine();
        var contracts = results.SelectMany(r => r.ByContract.Keys).Distinct().OrderBy(x => x).ToList();
        md.AppendLine("| contract | " + string.Join(" | ", results.Where(r => r.Kind != "").Select(r => System.IO.Path.GetFileName(r.Name))) + " |");
        md.AppendLine("|---|" + string.Concat(results.Where(r => r.Kind != "").Select(_ => "---|")));
        foreach (var c in contracts)
            md.AppendLine($"| {c} | " + string.Join(" | ", results.Where(r => r.Kind != "").Select(r => r.ByContract.TryGetValue(c, out var v) ? $"{v.Functions} / {v.Sites} [{v.Implemented}]" : "-")) + " |");

        md.AppendLine().AppendLine("## Imports").AppendLine();
        var all = imports.Values.ToList();
        md.AppendLine($"{all.Count} distinct imports (dll!name) over {all.Select(a => a.Dll).Distinct().Count()} DLLs; implemented by the Win32 component: {all.Count(a => a.Status == "implemented")}; satisfied by another SDK file: {all.Count(a => a.Status == "sdk-local")}; absent: {all.Count(a => a.Status == "absent")}. Sites = static `call`/`jmp` through the IAT slot or a one-instruction `jmp [slot]` thunk, summed over files.").AppendLine();
        md.AppendLine("| DLL | distinct functions | implemented | absent | sdk-local | static call sites | of which absent |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var g in all.GroupBy(a => a.Dll).OrderByDescending(g => g.Sum(a => a.Sites)))
            md.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(a => a.Status == "implemented")} | {g.Count(a => a.Status == "absent")} | {g.Count(a => a.Status == "sdk-local")} | {g.Sum(a => a.Sites)} | {g.Where(a => a.Status == "absent").Sum(a => a.Sites)} |");

        md.AppendLine().AppendLine("### Absent imports grouped into contracts").AppendLine();
        md.AppendLine("| contract | absent functions | absent call sites | functions with a call site in the entry-point closure (EXE entry or DllMain) | files |").AppendLine("|---|---|---|---|---|");
        foreach (var g in all.Where(a => a.Status == "absent").GroupBy(a => Contract(a.Dll, a.Name)).OrderByDescending(g => g.Sum(a => a.Sites)))
            md.AppendLine($"| {g.Key} | {g.Count()} | {g.Sum(a => a.Sites)} | {g.Count(a => a.StartupSites > 0)} | {g.SelectMany(a => a.Files).Distinct().Count()} |");

        md.AppendLine().AppendLine("### Absent imports, ranked by static call sites (top 80)").AppendLine();
        md.AppendLine("| rank | dll!function | contract | call sites | files |").AppendLine("|---|---|---|---|---|");
        rank = 1;
        foreach (var a in all.Where(a => a.Status == "absent").OrderByDescending(a => a.Sites).ThenBy(a => a.Dll).ThenBy(a => a.Name).Take(80))
            md.AppendLine($"| {rank++} | {a.Dll}!{a.Name} | {Contract(a.Dll, a.Name)} | {a.Sites} | {a.Files.Count} |");

        md.AppendLine().AppendLine("### Implemented imports by service (distinct functions / call sites)").AppendLine();
        md.AppendLine("| service | functions | call sites |").AppendLine("|---|---|---|");
        foreach (var g in all.Where(a => a.Status == "implemented").GroupBy(a => a.Service ?? "data").OrderByDescending(g => g.Sum(a => a.Sites)))
            md.AppendLine($"| {g.Key} | {g.Count()} | {g.Sum(a => a.Sites)} |");

        md.AppendLine().AppendLine("### Absent imports, complete list (dll!function xsites)").AppendLine();
        foreach (var g in all.Where(a => a.Status == "absent").GroupBy(a => a.Dll).OrderBy(g => g.Key))
            md.AppendLine($"- **{g.Key}** ({g.Count()}): " + string.Join(", ", g.OrderByDescending(a => a.Sites).ThenBy(a => a.Name).Select(a => $"{a.Name} x{a.Sites}")));

        md.AppendLine().AppendLine("## EXE startup closure").AppendLine();
        md.AppendLine("Imports that have at least one call site in a function reachable from the entry point by direct calls (for an EXE that is the CRT startup plus the whole statically reachable program).").AppendLine();
        md.AppendLine("| exe | functions (closure / all) | imports reached | of which absent | absent list |").AppendLine("|---|---|---|---|---|");
        foreach (var e in exeStartup)
            md.AppendLine($"| {e.File} | {e.ClosureFunctions} / {e.Functions} | {e.Reached} | {e.ReachedMissing} | {string.Join(", ", e.Missing.Select(a => a.Dll + "!" + a.Name))} |");
    }
}
