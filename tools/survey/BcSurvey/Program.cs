using System.Text;
using BcSurvey;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Unpack;

// usage:
//   BcSurvey seh  --out seh.md  [--unpack-cache dir] 'label=[unpack:]path|testsource,testsource|importer.dll,importer.dll' ...   ("unpack:" runs the controlled unpacker first)
//   BcSurvey sdk  <directory> --out sdk.md [--json sdk.json]                                     (every PE32 file found by its MZ header; nothing is executed)
if (args.Length < 1) { Console.Error.WriteLine("usage: BcSurvey seh|sdk ..."); return 2; }
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var out_ = Opt("--out");
var sb = new StringBuilder();
if (args[0] == "seh")
{
    var cache = Opt("--unpack-cache") ?? Path.Combine(Path.GetTempPath(), "bcsurvey-unpack");
    var skip = new HashSet<string> { "--out", "--unpack-cache" };
    for (var a = 1; a < args.Length; a++)
    {
        if (skip.Contains(args[a])) { a++; continue; }
        var spec = args[a]; var eq = spec.IndexOf('=');
        var label = spec[..eq]; var parts = spec[(eq + 1)..].Split('|');   // [unpack:]path | test sources (comma separated) | importing DLLs (comma separated)
        var path = parts[0];
        var sources = parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        var via = parts.Length > 2 ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        if (path.StartsWith("unpack:"))
        {
            path = path[7..];
            var (derived, _, hit) = UnpackBuild.Prepare(path, cache, new UnpackOptions());
            sb.AppendLine($"(unpacked `{Path.GetFileName(path)}` -> derived image, cache hit: {hit})").AppendLine();
            path = derived;
        }
        Seh.Run(label, path, sources, via, sb);
    }
}
else if (args[0] == "sdk")
{
    Sdk.Run(args[1], sb, Opt("--json"));
}
else if (args[0] == "startup")
{
    // BcSurvey startup <pe> [export-name|#ordinal ...] --out f.tsv : static start-up closure of the image (nothing is executed)
    var names = new List<string>();
    for (var i = 2; i < args.Length; i++) { if (args[i] == "--out") { i++; continue; } names.Add(args[i]); }
    Startup.Run(args[1], names, sb);
}
else if (args[0] == "dump")
{
    // BcSurvey dump <pe> <rva-hex> [count]  : disassembles the recovered instruction stream from an RVA (read-only)
    var img = PeReader.Read(File.ReadAllBytes(args[1]));
    var cfg = BlitzCS.BinaryCompiler.Lifting.ControlFlowGraph.Build(img, img.MapImage());
    var from = Convert.ToUInt32(args[2], 16); var n = args.Length > 3 ? int.Parse(args[3]) : 40;
    foreach (var g in cfg.Instructions.Where(x => x.Key >= from).Take(n).Select(x => x.Value))
        sb.AppendLine($"{g.Rva:X8}{(g.Speculative ? " s" : "  ")} {g.Instr}");
}
else { Console.Error.WriteLine("unknown command"); return 2; }
if (out_ is not null) File.WriteAllText(out_, sb.ToString()); else Console.Write(sb);
return 0;
