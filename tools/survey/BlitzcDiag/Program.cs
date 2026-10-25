using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using Iced.Intel;

// BlitzcDiag <exe> --target <os> [--base 0xHEX] [--mode bcrun|survey] [--stubs N]
// bcrun : options as tools/BcRun/BcRunner.cs (base from ModuleBaseAllocator.Allocate unless --base; Release; DynamicStubs 256 if LoadLibrary/GetProcAddress imported)
// survey: options as tools/survey/BcSurvey/Sdk.cs (base = ModuleBaseAllocator.RequiredBase(windows-x64); default profile; no stubs)
string? exe = null, target = null, mode = "bcrun", rvasOut = null; uint? baseArg = null; int? stubsArg = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--target": target = args[++i]; break;
        case "--base": baseArg = Convert.ToUInt32(args[++i].Replace("0x", ""), 16); break;
        case "--rvas": rvasOut = args[++i]; break;
        case "--mode": mode = args[++i]; break;
        case "--stubs": stubsArg = int.Parse(args[++i]); break;
        default: exe = args[i]; break;
    }
}
if (exe is null) { Console.Error.WriteLine("usage: BlitzcDiag <exe> --target <os> [--base 0x..] [--mode bcrun|survey] [--stubs N]"); return 2; }
var path = Path.GetFullPath(exe);
var bytes = File.ReadAllBytes(path);
var image = PeReader.Read(bytes);
var name = Path.GetFileName(path);
var tgt = BinaryTargets.TryNormalize(target) ?? BinaryTargets.Host;
var loaderImports = new[] { "LoadLibraryA", "LoadLibraryW", "LoadLibraryExA", "LoadLibraryExW", "GetProcAddress" };
bool importsLoader = image.Imports.Any(i => i.Name is not null && loaderImports.Contains(i.Name));
uint guestBase;
BinaryCompilerOptions options;
if (mode == "survey")
{
    guestBase = baseArg ?? ModuleBaseAllocator.RequiredBase(name, image, BinaryTargets.WindowsX64) ?? image.ImageBase;
    options = new BinaryCompilerOptions(TargetOs: tgt) { LoadBase = guestBase };
}
else
{
    guestBase = baseArg ?? ModuleBaseAllocator.Allocate([(name, image)], null, tgt)[0].Base;
    options = new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: tgt, LoadBase: guestBase) { DynamicStubs = stubsArg ?? (importsLoader ? 256 : 0) };
}
Console.WriteLine($"image={name} preferredBase=0x{image.ImageBase:X8} target={tgt} mode={mode} LoadBase=0x{guestBase:X8} Profile={options.Profile} DynamicStubs={options.DynamicStubs} importsLoader={importsLoader} imageBytes={bytes.Length}");
var art = X64Translator.Translate(image, options, "diag");
var counts = art.Diagnostics.GroupBy(d => $"{d.Severity}:{d.Code}").OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}");
Console.WriteLine("counts: " + string.Join(", ", counts));
if (rvasOut is not null)
{
    var rb = guestBase != image.ImageBase ? image.Rebase(guestBase) : image;
    var cfg = BlitzCS.BinaryCompiler.Lifting.ControlFlowGraph.Build(rb, rb.MapImage());
    File.WriteAllLines(rvasOut, cfg.Instructions.Keys.Select(k => k.ToString("X8")));
}
var rebased = guestBase != image.ImageBase ? image.Rebase(guestBase) : image;
var mapped = rebased.MapImage();
foreach (var d in art.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
{
    var line = $"{d.Severity,-7} {d.Code} RVA=" + (d.GuestRva is { } r ? $"0x{r:X}" : "-") + " :: " + d.Message;
    Console.WriteLine(line);
    if (d.Severity == DiagnosticSeverity.Error && d.GuestRva is { } rva && rva < mapped.Length)
    {
        var n = (int)Math.Min(8, mapped.Length - rva);
        var window = mapped.AsSpan((int)rva, n).ToArray();
        var dd = Decoder.Create(32, window, (ulong)guestBase + rva);
        var sb = new System.Text.StringBuilder("        bytes=" + Convert.ToHexString(window) + " decoded:");
        while (dd.IP < (ulong)guestBase + rva + (ulong)n) { var ins = dd.Decode(); sb.Append($" [{ins.IP:X}: {ins}]"); if (ins.IsInvalid) break; }
        Console.WriteLine(sb.ToString());
    }
}
return 0;
