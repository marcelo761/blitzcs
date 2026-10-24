using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// Return-ABI differential tests (tools/x86_oracle/lib/ret_abi.c): every return class, in cdecl and stdcall, with edge values (NaN payloads, -0, inf,
/// denormals, INT64_MIN). The ELF32 oracle makes the typed native call and prints an FNV hash of the result bytes; the translated guest is called with
/// the matching GuestFunction API and the hash of its result bytes must be identical.
/// </summary>
[Collection("GuestRuntime")]
public sealed class ReturnDifferentialTests
{
    private enum Kind { F32, F64, Ld, I64, Struct, Sret, Xmm0F32, Xmm0F64 }

    // name -> (kind, convention, declared stack words, struct size)
    private static readonly Dictionary<string, (Kind Kind, GuestCallingConvention Conv, int Words, int Size)> Functions = new()
    {
        ["r_f32_c"] = (Kind.F32, GuestCallingConvention.Cdecl, 2, 4), ["r_f32_s"] = (Kind.F32, GuestCallingConvention.Stdcall, 2, 4), ["r_f32id_c"] = (Kind.F32, GuestCallingConvention.Cdecl, 1, 4),
        ["r_f64_c"] = (Kind.F64, GuestCallingConvention.Cdecl, 4, 8), ["r_f64_s"] = (Kind.F64, GuestCallingConvention.Stdcall, 4, 8), ["r_f64r_c"] = (Kind.F64, GuestCallingConvention.Cdecl, 4, 8),
        ["r_f64id_c"] = (Kind.F64, GuestCallingConvention.Cdecl, 2, 8),
        ["r_ld_c"] = (Kind.Ld, GuestCallingConvention.Cdecl, 4, 10), ["r_ld_s"] = (Kind.Ld, GuestCallingConvention.Stdcall, 4, 10), ["r_ldid_c"] = (Kind.Ld, GuestCallingConvention.Cdecl, 2, 10),
        ["r_i64_c"] = (Kind.I64, GuestCallingConvention.Cdecl, 2, 8), ["r_i64_s"] = (Kind.I64, GuestCallingConvention.Stdcall, 2, 8), ["r_i64id_c"] = (Kind.I64, GuestCallingConvention.Cdecl, 2, 8),
        ["r_u64id_s"] = (Kind.I64, GuestCallingConvention.Stdcall, 2, 8),
        ["r_s1_c"] = (Kind.Struct, GuestCallingConvention.Cdecl, 2, 1), ["r_s1_s"] = (Kind.Struct, GuestCallingConvention.Stdcall, 2, 1), ["r_s2_c"] = (Kind.Struct, GuestCallingConvention.Cdecl, 2, 2),
        ["r_s4_c"] = (Kind.Struct, GuestCallingConvention.Cdecl, 2, 4), ["r_s4_s"] = (Kind.Struct, GuestCallingConvention.Stdcall, 2, 4),
        ["r_s8_c"] = (Kind.Struct, GuestCallingConvention.Cdecl, 2, 8), ["r_s8_s"] = (Kind.Struct, GuestCallingConvention.Stdcall, 2, 8),
        ["r_big_c"] = (Kind.Sret, GuestCallingConvention.Cdecl, 3, 20), ["r_big_s"] = (Kind.Sret, GuestCallingConvention.Stdcall, 3, 20),
        ["r_x0f_c"] = (Kind.Xmm0F32, GuestCallingConvention.Cdecl, 2, 4), ["r_x0d_s"] = (Kind.Xmm0F64, GuestCallingConvention.Stdcall, 2, 8),
    };

    public static TheoryData<string, OptimizationProfile> Cases()
    {
        var d = new TheoryData<string, OptimizationProfile>();
        foreach (var p in TranslatorHarness.Profiles) d.Add("ret_abi", p);
        return d;
    }

    private static uint Hash(ReadOnlySpan<byte> bytes) => OracleDifferential.Fnv(bytes);
    private static byte[] Bytes(ulong v, int n) { var b = new byte[n]; for (var i = 0; i < n; i++) b[i] = (byte)(v >> (8 * i)); return b; }

    [OracleTheory, MemberData(nameof(Cases))]
    public void Every_return_class_matches_the_x86_oracle(string lib, OptimizationProfile profile)
    {
        var art = OracleRunner.Build(lib);
        var oracle = OracleRunner.Run(lib);
        var image = OracleDifferential.LoadImage(art);
        var tr = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "ret-" + lib);
        Assert.DoesNotContain(tr.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        using var module = GuestModule.Load(tr, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        var fns = Functions.ToDictionary(f => f.Key, f => module.Bind(f.Key, f.Value.Conv, f.Value.Words));
        var perFunction = new Dictionary<string, int>();
        foreach (var v in oracle.Vectors)
        {
            var (kind, _, _, size) = Functions[v.Function];
            var fn = fns[v.Function];
            uint got;
            string shown;
            switch (kind)
            {
                case Kind.F32: { var r = fn.Invoke(GuestReturn.St0, v.Args); got = Hash(BitConverter.GetBytes(r.St0Single)); shown = r.St0Single.ToString("R"); break; }
                case Kind.F64: { var r = fn.Invoke(GuestReturn.St0, v.Args); got = Hash(BitConverter.GetBytes(r.St0Double)); shown = r.St0Double.ToString("R"); break; }
                case Kind.Ld:
                {
                    var r = fn.Invoke(GuestReturn.St0, v.Args);
                    got = Hash([.. BitConverter.GetBytes(r.St0Mantissa), .. BitConverter.GetBytes(r.St0SignExponent)]); shown = $"{r.St0SignExponent:X4}:{r.St0Mantissa:X16}"; break;
                }
                case Kind.I64: { var r = fn.CallUInt64(v.Args); got = Hash(BitConverter.GetBytes(r)); shown = r.ToString("X16"); break; }
                case Kind.Struct: { var r = fn.CallStructInRegisters(size, v.Args); got = Hash(r); shown = Convert.ToHexString(r); break; }
                case Kind.Sret: { var r = fn.CallStructViaHiddenPointer(size, v.Args); got = Hash(r); shown = Convert.ToHexString(r); break; }
                case Kind.Xmm0F32: { var r = fn.CallXmm0Single(v.Args); got = Hash(BitConverter.GetBytes(r)); shown = r.ToString("R"); break; }
                default: { var r = fn.CallXmm0Double(v.Args); got = Hash(BitConverter.GetBytes(r)); shown = r.ToString("R"); break; }
            }
            Assert.True(got == v.Result, $"{FailureClass.Translation}: {lib}/{profile} vector #{v.Index} {v.Function}({string.Join(", ", v.Args.Select(a => "0x" + a.ToString("x")))}): result hash oracle=0x{v.Result:x} guest=0x{got:x} (guest result {shown})");
            perFunction[v.Function] = perFunction.GetValueOrDefault(v.Function) + 1;
        }
        Assert.Equal(Functions.Count, perFunction.Count);
        Assert.All(perFunction, p => Assert.True(p.Value >= 91, p.Key));
    }
}
