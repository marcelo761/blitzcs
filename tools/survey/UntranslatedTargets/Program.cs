using System.Text;
using Decoder = Iced.Intel.Decoder;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Lifting;
using Iced.Intel;

// Usage: UntranslatedTargets <image>... 
// For each PE image: translate windows-x64 at the allocator's base (as the survey does) and, for every
// BC_UNTRANSLATED_TARGET diagnostic, print the branch RVA, its Speculative flag, the target RVA/section and
// a classification of the bytes at the target (decoded as 32-bit x86, as the CFG does).
static class Program
{
    static readonly System.Text.RegularExpressions.Regex VaRx = new(@"0x([0-9A-Fa-f]{8})", System.Text.RegularExpressions.RegexOptions.Compiled);

    static int Main(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("image\tbranch_rva\tbranch_spec\ttarget_rva\tsection\texec\tcode_like\tclean_insns\treason\tprev_insn_end_eq_target\tprev_byte\tprev_insn_spec\tfn_entry_nonspec\tbytes_at_target\tfirst_insns\tbranch_insn\tbranch_bytes");
        foreach (var path in args)
        {
            var name = Path.GetFileName(path);
            PeImage image;
            try { image = PeReader.Read(File.ReadAllBytes(path)); }
            catch (Exception e) { Console.Error.WriteLine($"{name}: not a PE: {e.Message}"); continue; }
            uint? loadBase = ModuleBaseAllocator.RequiredBase(name, image, BinaryTargets.WindowsX64);
            var rebased = loadBase is { } lb && lb != image.ImageBase ? image.Rebase(lb) : image;
            var baseVa = loadBase ?? image.ImageBase;
            var art = X64Translator.Translate(image, new BinaryCompilerOptions(TargetOs: BinaryTargets.WindowsX64) { LoadBase = loadBase }, "untranslated-targets");
            var mapped = rebased.MapImage();
            var cfg = ControlFlowGraph.Build(rebased, mapped);
            if (Environment.GetEnvironmentVariable("DUMP_RVA") is { } dr)
            {
                uint from = Convert.ToUInt32(dr, 16);
                foreach (var g in cfg.Instructions.Values.Where(g => g.Rva >= from && g.Rva < from + 64))
                    Console.Error.WriteLine($"  {name} 0x{g.Rva:X} len={g.Length} spec={g.Speculative} {g.Instr}");
                Console.Error.WriteLine($"  undecodable near: {string.Join(",", cfg.Undecodable.Keys.Where(k => k >= from && k < from + 64).Select(k => "0x" + k.ToString("X")))}");
            }
            if (Environment.GetEnvironmentVariable("REFS_RVA") is { } rr)
            {
                uint want = Convert.ToUInt32(rr, 16);
                foreach (var g in cfg.Instructions.Values)
                {
                    var ins2 = g.Instr;
                    if (ins2.Op0Kind == OpKind.NearBranch32 && unchecked(ins2.NearBranchTarget - rebased.ImageBase) == want)
                        Console.Error.WriteLine($"  ref 0x{want:X} <- 0x{g.Rva:X} spec={g.Speculative} {ins2}");
                    if (g.Rva + (uint)g.Length == want) Console.Error.WriteLine($"  fallthrough into 0x{want:X} from 0x{g.Rva:X} spec={g.Speculative} {ins2}");
                }
                foreach (var fe in cfg.FunctionEntries.Where(f => f >= want - 0x200 && f <= want)) Console.Error.WriteLine($"  function entry 0x{fe:X}");
                foreach (var l in cfg.Leaders.Where(f => f >= want - 0x10 && f <= want + 0x10)) Console.Error.WriteLine($"  leader 0x{l:X}");
            }
            int total = 0, spec = 0, nonspec = 0, code = 0, data = 0;
            foreach (var d in art.Diagnostics.Where(x => x.Code == "BC_UNTRANSLATED_TARGET"))
            {
                total++;
                uint branch = d.GuestRva ?? 0;
                bool bspec = cfg.Instructions.TryGetValue(branch, out var bi) ? bi.Speculative : true;
                if (bspec) spec++; else nonspec++;
                var m = VaRx.Match(d.Message);
                uint va = uint.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                uint t = unchecked(va - baseVa);
                var sec = rebased.Sections.FirstOrDefault(s => t >= s.VirtualAddress && t < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
                string secName = sec?.Name ?? "none";
                bool exec = sec?.IsExecutable ?? false;
                // classification: decode up to 24 instructions at the target
                var (codeLike, clean, reason, first) = Classify(mapped, t);
                // what precedes the target
                var prev = cfg.Instructions.Values.Where(g => g.Rva < t).OrderByDescending(g => g.Rva).FirstOrDefault();
                bool fallEq = prev is not null && prev.NextRva == t;
                string prevSpec = prev is null ? "-" : (prev.Speculative ? "spec" : "nonspec");
                byte pb = t > 0 && t - 1 < mapped.Length ? mapped[t - 1] : (byte)0;
                // is the target itself a function entry of a non-speculative instruction (should be impossible for an untranslated target)?
                string fnNon = cfg.FunctionEntries.Contains(t) ? (cfg.Instructions.TryGetValue(t, out var ti) && !ti.Speculative ? "yes" : "spec") : "no";
                if (codeLike) code++; else data++;
                string bytes = t + 32 <= mapped.Length ? Convert.ToHexString(mapped.AsSpan((int)t, 32)) : "?";
                sb.AppendLine($"{name}\t0x{branch:X}\t{(bspec ? "spec" : "NONSPEC")}\t0x{t:X}\t{secName}\t{(exec ? "x" : "-")}\t{(codeLike ? "CODE" : "DATA")}\t{clean}\t{reason}\t{(fallEq ? "yes" : "no")}\t{pb:X2}\t{prevSpec}\t{fnNon}\t{bytes}\t{first}\t{(cfg.Instructions.TryGetValue(branch, out var bx) ? bx.Instr.ToString() + " len=" + bx.Length + " next=0x" + bx.NextRva.ToString("X") : "NOT_IN_CFG")}\t{(branch + 8 <= mapped.Length ? Convert.ToHexString(mapped.AsSpan((int)branch, 8)) : "?")}");
            }
            Console.Error.WriteLine($"{name}: base=0x{baseVa:X8} untranslated_targets={total} speculative_branch={spec} nonspeculative_branch={nonspec} code_like={code} data_like={data}");
        }
        Console.Write(sb.ToString());
        return 0;
    }

    static (bool codeLike, int clean, string reason, string first) Classify(byte[] mapped, uint t)
    {
        if (t >= mapped.Length) return (false, 0, "outside image", "");
        var dec = Decoder.Create(32, new ByteArrayCodeReader(mapped, (int)t, (int)Math.Min(mapped.Length - t, 512u)), t);
        int clean = 0; bool flowSeen = false; string reason = "24 insns without ret/jmp/jcc";
        var parts = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            var ins = dec.Decode();
            if (ins.IsInvalid) { reason = $"invalid at +{ins.IP - t}"; break; }
            if (IsPrivileged(ins.Mnemonic)) { reason = $"privileged {ins.Mnemonic} at +{ins.IP - t}"; break; }
            clean++;
            if (parts.Count < 8) parts.Add($"{ins}");
            if (ins.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch) { flowSeen = true; if (clean >= 8) { reason = $"ret/jmp/jcc at +{ins.IP - t}"; break; } }
        }
        bool codeLike = clean >= 8 && flowSeen;
        if (!codeLike && flowSeen && clean < 8) reason = $"only {clean} clean insns before flow";
        return (codeLike, clean, reason.Replace('\t', ' '), string.Join(" ; ", parts));
    }

    static bool IsPrivileged(Mnemonic m) => m is Mnemonic.In or Mnemonic.Out or Mnemonic.Insb or Mnemonic.Insd or Mnemonic.Insw
        or Mnemonic.Outsb or Mnemonic.Outsd or Mnemonic.Outsw or Mnemonic.Cli or Mnemonic.Sti or Mnemonic.Hlt
        or Mnemonic.Bound or Mnemonic.Arpl or Mnemonic.Lds or Mnemonic.Les or Mnemonic.Aaa or Mnemonic.Aas
        or Mnemonic.Aad or Mnemonic.Aam or Mnemonic.Daa or Mnemonic.Das;
}
