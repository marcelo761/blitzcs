using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// Struct returns through the hidden pointer (thiscall, fastcall, stdcall; GCC's placement) and out-pointer parameters (int, float, double, int64, arrays,
/// structs, in/out, two outputs) against the x86 oracle (tools/x86_oracle/lib/abiout.c). The oracle hashes (return value | output bytes); the guest side
/// hashes the same bytes from GuestFunction.CallStruct* and GuestFunction.InvokeWithBuffers.
/// </summary>
[Collection("GuestRuntime")]
public sealed class AbiOutDifferentialTests
{
    private enum Kind { SretEcx, SretDefault, InRegs, Out }

    private static readonly Dictionary<string, (Kind Kind, GuestCallingConvention Conv, int Words, int Size)> Functions = new()
    {
        ["ts_big"] = (Kind.SretEcx, GuestCallingConvention.Thiscall, 4, 20),   // GCC thiscall: hidden pointer in ECX, 'this' on the stack
        ["fs_big"] = (Kind.SretDefault, GuestCallingConvention.Fastcall, 3, 20),
        ["ss_big"] = (Kind.SretDefault, GuestCallingConvention.Stdcall, 4, 20),
        ["ts_s8"] = (Kind.InRegs, GuestCallingConvention.Thiscall, 2, 8), ["fs_s8"] = (Kind.InRegs, GuestCallingConvention.Fastcall, 2, 8),
        ["ts_s3"] = (Kind.SretEcx, GuestCallingConvention.Thiscall, 3, 3), ["fs_s3"] = (Kind.SretDefault, GuestCallingConvention.Fastcall, 3, 3),
        ["o_divmod_s"] = (Kind.Out, GuestCallingConvention.Stdcall, 4, 0), ["o_i64_c"] = (Kind.Out, GuestCallingConvention.Cdecl, 3, 0),
        ["o_f32_t"] = (Kind.Out, GuestCallingConvention.Thiscall, 3, 0), ["o_f64_f"] = (Kind.Out, GuestCallingConvention.Fastcall, 4, 0),
        ["o_arr_c"] = (Kind.Out, GuestCallingConvention.Cdecl, 3, 0), ["o_inout_c"] = (Kind.Out, GuestCallingConvention.Cdecl, 2, 0),
        ["o_struct_s"] = (Kind.Out, GuestCallingConvention.Stdcall, 2, 0), ["o_two_t"] = (Kind.Out, GuestCallingConvention.Thiscall, 3, 0),
    };

    public static TheoryData<OptimizationProfile> Profiles()
    {
        var d = new TheoryData<OptimizationProfile>();
        foreach (var p in TranslatorHarness.Profiles) d.Add(p);
        return d;
    }

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
    private static byte[] U(uint v) => BitConverter.GetBytes(v);

    [OracleTheory, MemberData(nameof(Profiles))]
    public void Struct_returns_and_out_pointers_match_the_x86_oracle(OptimizationProfile profile)
    {
        var art = OracleRunner.Build("abiout");
        var oracle = OracleRunner.Run("abiout");
        var image = PeReader.Read(OracleDifferential.BuildFixture(art));
        var tr = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "abiout");
        Assert.DoesNotContain(tr.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        using var module = GuestModule.Load(tr, image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
        var fns = Functions.ToDictionary(f => f.Key, f => module.Bind(f.Key, f.Value.Conv, f.Value.Words));
        var data = new byte[oracle.DataLength];
        var seen = new HashSet<string>();
        foreach (var v in oracle.Vectors)
        {
            var (kind, _, _, size) = Functions[v.Function];
            var fn = fns[v.Function];
            var a = v.Args;
            byte[] bytes;
            switch (kind)
            {
                case Kind.SretEcx: bytes = fn.CallStructViaHiddenPointer(size, StructReturnPointer.Ecx, a); break;
                case Kind.SretDefault: bytes = fn.CallStructViaHiddenPointer(size, a); break;
                case Kind.InRegs: bytes = fn.CallStructInRegisters(size, a); break;
                default: bytes = OutBytes(fn, v.Function, a); break;
            }
            var got = OracleDifferential.Fnv(bytes);
            var where = $"{FailureClass.Translation}: abiout/{profile} vector #{v.Index} {v.Function}({string.Join(", ", a.Select(x => "0x" + x.ToString("x")))})";
            Assert.True(got == v.Result, $"{where}: oracle=0x{v.Result:x} guest=0x{got:x} ({Convert.ToHexString(bytes)})");
            module.Memory.Read(OracleDifferential.DataVa, data);
            Assert.True(OracleDifferential.Fnv(data) == v.DataHash, $"{where}: data hash differs");
            seen.Add(v.Function);
        }
        Assert.Equal(Functions.Count, seen.Count);
    }

    /// <summary>The same byte sequence the oracle driver hashes: return value, then the output buffers in the driver's order.</summary>
    private static byte[] OutBytes(GuestFunction fn, string name, uint[] a)
    {
        GuestCallOutcome o;
        switch (name)
        {
            case "o_divmod_s": o = fn.InvokeWithBuffers(GuestReturn.Eax, a[0], a[1], GuestArg.Out<uint>(), GuestArg.Out<uint>()); return Cat(U(o.Eax), o.Bytes(2), o.Bytes(3));
            case "o_i64_c": o = fn.InvokeWithBuffers(GuestReturn.Eax, a[0], a[1], GuestArg.Out<long>()); return Cat(U(o.Eax), BitConverter.GetBytes(o.GetInt64(2)));
            case "o_f32_t": o = fn.InvokeWithBuffers(GuestReturn.Eax, a[0], a[1], GuestArg.Out<float>()); return Cat(U(o.Eax), BitConverter.GetBytes(o.GetSingle(2)));
            case "o_f64_f": o = fn.InvokeWithBuffers(GuestReturn.Eax, a[0], a[1], a[2], GuestArg.Out<double>()); return Cat(U(o.Eax), BitConverter.GetBytes(o.GetDouble(3)));
            case "o_arr_c": o = fn.InvokeWithBuffers(GuestReturn.Eax, GuestArg.OutArray<uint>(16), a[0], a[1]); return Cat(U(o.Eax), o.Bytes(0));
            case "o_inout_c": o = fn.InvokeWithBuffers(GuestReturn.Eax, GuestArg.InOut(a[0]), a[1]); return Cat(U(o.Eax), o.Bytes(0));
            case "o_struct_s": o = fn.InvokeWithBuffers(GuestReturn.Eax, GuestArg.Out(20), a[0]); return Cat(U(o.Eax), o.Bytes(0));
            case "o_two_t": o = fn.InvokeWithBuffers(GuestReturn.Eax, a[0], GuestArg.Out<uint>(), GuestArg.Out<uint>()); return Cat(U(o.Eax), o.Bytes(1), o.Bytes(2));
            default: throw new InvalidOperationException(name);
        }
    }
}
