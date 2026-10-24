using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// Register-argument conventions against the x86 oracle (tools/x86_oracle/lib/regconv.c): thiscall (ECX = this) and fastcall (ECX, EDX) with 0..5 arguments,
/// nesting across thiscall/fastcall/stdcall/cdecl, register pressure, data-section side effects and int / float / double / int64 / long double returns.
/// The oracle makes the native typed call and prints a hash of the result; the guest is called through GuestFunction and must produce the same hash,
/// the same per-vector data hash and the same final data.
/// </summary>
[Collection("GuestRuntime")]
public sealed class ConvRegisterDifferentialTests
{
    private enum Kind { Int, F32, F64, Ld, I64 }

    private static readonly Dictionary<string, (Kind Kind, GuestCallingConvention Conv, int Words)> Functions = new()
    {
        ["t_a0"] = (Kind.Int, GuestCallingConvention.Thiscall, 1), ["t_a1"] = (Kind.Int, GuestCallingConvention.Thiscall, 2),
        ["t_a2"] = (Kind.Int, GuestCallingConvention.Thiscall, 3), ["t_a5"] = (Kind.Int, GuestCallingConvention.Thiscall, 6),
        ["f_a0"] = (Kind.Int, GuestCallingConvention.Fastcall, 0), ["f_a1"] = (Kind.Int, GuestCallingConvention.Fastcall, 1),
        ["f_a2"] = (Kind.Int, GuestCallingConvention.Fastcall, 2), ["f_a3"] = (Kind.Int, GuestCallingConvention.Fastcall, 3), ["f_a5"] = (Kind.Int, GuestCallingConvention.Fastcall, 5),
        ["n_this"] = (Kind.Int, GuestCallingConvention.Thiscall, 3), ["n_cdecl_calls_this"] = (Kind.Int, GuestCallingConvention.Cdecl, 2),
        ["n_std_calls_fast"] = (Kind.Int, GuestCallingConvention.Stdcall, 2), ["n_pressure"] = (Kind.Int, GuestCallingConvention.Thiscall, 3),
        ["f_pressure"] = (Kind.Int, GuestCallingConvention.Fastcall, 3),
        ["tr_f32"] = (Kind.F32, GuestCallingConvention.Thiscall, 2), ["fr_f32"] = (Kind.F32, GuestCallingConvention.Fastcall, 2),
        ["tr_f64"] = (Kind.F64, GuestCallingConvention.Thiscall, 3), ["fr_f64"] = (Kind.F64, GuestCallingConvention.Fastcall, 3),
        ["tr_i64"] = (Kind.I64, GuestCallingConvention.Thiscall, 3), ["fr_i64"] = (Kind.I64, GuestCallingConvention.Fastcall, 3),
        ["tr_f32_nested"] = (Kind.F32, GuestCallingConvention.Thiscall, 2), ["fr_ld"] = (Kind.Ld, GuestCallingConvention.Fastcall, 3),
    };

    public static TheoryData<OptimizationProfile> Profiles()
    {
        var d = new TheoryData<OptimizationProfile>();
        foreach (var p in TranslatorHarness.Profiles) d.Add(p);
        return d;
    }

    private static uint Hash(ReadOnlySpan<byte> b) => OracleDifferential.Fnv(b);

    [OracleTheory, MemberData(nameof(Profiles))]
    public void Thiscall_and_fastcall_match_the_x86_oracle(OptimizationProfile profile)
    {
        var art = OracleRunner.Build("regconv");
        var oracle = OracleRunner.Run("regconv");
        var image = OracleDifferential.LoadImage(art);
        var tr = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "regconv");
        Assert.DoesNotContain(tr.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        using var module = GuestModule.Load(tr, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        var fns = Functions.ToDictionary(f => f.Key, f => module.Bind(f.Key, f.Value.Conv, f.Value.Words));
        var data = new byte[oracle.DataLength];
        var seen = new HashSet<string>();
        foreach (var v in oracle.Vectors)
        {
            var kind = Functions[v.Function].Kind;
            var fn = fns[v.Function];
            uint got; string shown;
            switch (kind)
            {
                case Kind.Int: { var r = fn.Call(v.Args); got = r; shown = r.ToString("x"); break; }
                case Kind.F32: { var r = fn.CallSingle(v.Args); got = Hash(BitConverter.GetBytes(r)); shown = r.ToString("R"); break; }
                case Kind.F64: { var r = fn.CallDouble(v.Args); got = Hash(BitConverter.GetBytes(r)); shown = r.ToString("R"); break; }
                case Kind.I64: { var r = fn.CallUInt64(v.Args); got = Hash(BitConverter.GetBytes(r)); shown = r.ToString("X16"); break; }
                default:
                {
                    var r = fn.Invoke(GuestReturn.St0, v.Args);
                    got = Hash([.. BitConverter.GetBytes(r.St0Mantissa), .. BitConverter.GetBytes(r.St0SignExponent)]); shown = $"{r.St0SignExponent:X4}:{r.St0Mantissa:X16}"; break;
                }
            }
            var where = $"{FailureClass.Translation}: regconv/{profile} vector #{v.Index} {v.Function}({string.Join(", ", v.Args.Select(a => "0x" + a.ToString("x")))})";
            Assert.True(got == v.Result, $"{where}: result oracle=0x{v.Result:x} guest=0x{got:x} ({shown})");
            module.Memory.Read(image.ImageBase + PeFixtureBuilder.DataRva, data);
            Assert.True(Hash(data) == v.DataHash, $"{where}: data hash oracle=0x{v.DataHash:x} guest=0x{Hash(data):x}");
            seen.Add(v.Function);
        }
        Assert.Equal(Functions.Count, seen.Count);
        module.Memory.Read(image.ImageBase + PeFixtureBuilder.DataRva, data);
        Assert.Equal(oracle.FinalData, data);
    }
}
