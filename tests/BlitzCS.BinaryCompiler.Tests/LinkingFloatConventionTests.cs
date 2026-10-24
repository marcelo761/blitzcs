using System.Diagnostics;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.CLI;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// End-to-end linking of float returns and register conventions (thiscall, fastcall): a .bb program calls hand-assembled PE32 exports through the generated
/// binding; the child dotnet process runs the generated program and its stdout is compared.
/// </summary>
[Collection("GuestRuntime")]
public sealed class LinkingFloatConventionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitzcs-linking-float").FullName;
    private readonly string? previousCache = Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE");

    public LinkingFloatConventionTests() => Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", Path.Combine(root, "cache"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", previousCache);
        Directory.Delete(root, recursive: true);
    }

    private string Cli(string source, string catalog, out int exit)
    {
        File.WriteAllText(Path.Combine(root, "main.bb"), source);
        File.WriteAllText(Path.Combine(root, "catalog.json"), catalog);
        var stdout = new StringWriter(); var stderr = new StringWriter();
        exit = CliApplication.Run(["build", Path.Combine(root, "main.bb"), "-o", Path.Combine(root, "out", "main.dll"), "--externals", Path.Combine(root, "catalog.json")], stdout, stderr);
        return stdout + stderr.ToString();
    }

    private string RunProgram()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(root, "out", "main.dll"));
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return text.Replace("\r\n", "\n");
    }

    private static string Entry(string name, string export, string[] parameters, string returnType, string? convention) =>
        $"{{\"name\":\"{name}\",\"parameters\":[{string.Join(",", parameters.Select(p => $"\"{p}\""))}],\"returnType\":\"{returnType}\","
        + $"\"binary\":{{\"library\":\"lib.dll\",\"export\":\"{export}\"" + (convention is null ? "" : $",\"convention\":\"{convention}\"") + "}}";

    private static string Catalog(params string[] entries) => "{\"functions\":[" + string.Join(",", entries) + "]}";

    private void Lib(PeFixtureBuilder builder) => File.WriteAllBytes(Path.Combine(root, "lib.dll"), builder.Build());

    private const uint Data = PeFixtureBuilder.ImageBase + PeFixtureBuilder.DataRva;

    [Fact]
    public void FloatReturnsFromCdeclAndStdcallExportsRoundTripThroughSt0()
    {
        // cdecl float add(float a, float b): fld a; fadd b ; stdcall float neg(float a): fld a; fchs; ret 4
        var code = TranslatorHarness.Asm(a =>
        {
            a.fld(__dword_ptr[esp + 4]); a.fadd(__dword_ptr[esp + 8]); a.ret();
            a.nop(); a.nop();
        }, out _);
        var neg = TranslatorHarness.Asm(a => { a.fld(__dword_ptr[esp + 4]); a.fchs(); a.ret(4); });
        var all = code.Concat(Enumerable.Repeat((byte)0x90, 0x20 - code.Length)).Concat(neg).ToArray();
        Lib(new PeFixtureBuilder().WithCode(all).Export("_fadd", 0).Export("_fneg@4", 0x20));
        var text = Cli("Print FAdd(1.5, 2.25)\nPrint FNeg(0.5)\nx# = FAdd(0.1, 0.2)\nIf x# > 0.29 And x# < 0.31 Then Print \"ok\"\n",
            Catalog(Entry("FAdd", "_fadd", ["Float", "Float"], "Float", "cdecl"), Entry("FNeg", "_fneg", ["Float"], "Float", null)), out var exit);
        Assert.Equal(0, exit);
        Assert.Equal("3.75\n-0.5\nok\n", RunProgram());
        _ = text;
    }

    [Fact]
    public void ExtendedPrecisionSt0IsNarrowedDirectlyToSingleNotViaDouble()
    {
        // returns the 80-bit value 1 + 2^-24 + 2^-60 (just above the float tie): direct rounding gives 1 + 2^-23, rounding through double gives 1.0
        var data = new byte[0x100];
        BitConverter.GetBytes(0x8000008000000008UL).CopyTo(data, 0); BitConverter.GetBytes((ushort)0x3FFF).CopyTo(data, 8);
        var code = TranslatorHarness.Asm(a => { a.fld(__tbyte_ptr[(int)Data]); a.ret(); });
        Lib(new PeFixtureBuilder().WithCode(code).WithData(data).Export("_ext", 0));
        Cli("x# = Ext()\nIf x# > 1.0 Then Print \"above\" Else Print \"tie\"\n", Catalog(Entry("Ext", "_ext", [], "Float", "cdecl")), out var exit);
        Assert.Equal(0, exit);
        Assert.Equal("above\n", RunProgram());
    }

    [Fact]
    public void FloatReturnMismatchesAreRuntimeErrorsNotWrongValues()
    {
        // declared Float but the callee returns an integer (x87 stack empty), and declared Integer but the callee returns in ST0
        var ints = TranslatorHarness.Asm(a => { a.mov(eax, 5); a.ret(); });
        var flt = TranslatorHarness.Asm(a => { a.fld1(); a.ret(); });
        Lib(new PeFixtureBuilder().WithCode(ints.Concat(Enumerable.Repeat((byte)0x90, 0x10 - ints.Length)).Concat(flt).ToArray()).Export("_i", 0).Export("_f", 0x10));
        Cli("Print AsFloat()\n", Catalog(Entry("AsFloat", "_i", [], "Float", "cdecl")), out var exit);
        Assert.Equal(0, exit);
        Assert.Contains("empty x87 stack", RunProgram());
        Cli("Print AsInt()\n", Catalog(Entry("AsInt", "_f", [], "Integer", "cdecl")), out exit);
        Assert.Equal(0, exit);
        Assert.Contains("x87", RunProgram());
    }

    [Fact]
    public void ThiscallBindingPassesTheFirstIntegerInEcx()
    {
        // thiscall int f(this, a): eax = ecx*100 + a ; ret 4        thiscall float g(this, x): fld x ; ret 4
        var f = TranslatorHarness.Asm(a => { a.imul(eax, ecx, 100); a.add(eax, __dword_ptr[esp + 4]); a.ret(4); });
        var g = TranslatorHarness.Asm(a => { a.fld(__dword_ptr[esp + 4]); a.ret(4); });
        Lib(new PeFixtureBuilder().WithCode(f.Concat(Enumerable.Repeat((byte)0x90, 0x20 - f.Length)).Concat(g).ToArray()).Export("obj_f", 0).Export("obj_g", 0x20));
        Cli("Print ObjF(3, 4)\nPrint ObjF(7, -1)\nPrint ObjG(9, 2.5)\n",
            Catalog(Entry("ObjF", "obj_f", ["Integer", "Integer"], "Integer", "thiscall"), Entry("ObjG", "obj_g", ["Integer", "Float"], "Float", "thiscall")), out var exit);
        Assert.Equal(0, exit);
        Assert.Equal("304\n699\n2.5\n", RunProgram());
    }

    [Fact]
    public void FastcallBindingFollowsTheMsvcRegisterRuleAndTheDecoration()
    {
        // @fc@12: fastcall int(a, b, c): ecx*100 + edx*10 + [esp+4] ; ret 4  (convention inferred from the decoration)
        var fc = TranslatorHarness.Asm(a => { a.imul(eax, ecx, 100); a.imul(edx, edx, 10); a.add(eax, edx); a.add(eax, __dword_ptr[esp + 4]); a.ret(4); });
        // @fm@12: fastcall int(int a, float b, int c): a in ECX, c in EDX, the float stays on the stack -> a*100 + c*10000 + int(b)
        var fm = TranslatorHarness.Asm(a =>
        {
            a.sub(esp, 4); a.fld(__dword_ptr[esp + 8]); a.fistp(__dword_ptr[esp]); a.pop(eax);
            a.imul(ecx, ecx, 100); a.add(eax, ecx); a.imul(edx, edx, 10000); a.add(eax, edx); a.ret(4);
        });
        Lib(new PeFixtureBuilder().WithCode(fc.Concat(Enumerable.Repeat((byte)0x90, 0x30 - fc.Length)).Concat(fm).ToArray()).Export("@fc@12", 0).Export("@fm@12", 0x30));
        Cli("Print Fc(1, 2, 3)\nPrint Fm(1, 2.0, 3)\n",
            Catalog(Entry("Fc", "fc", ["Integer", "Integer", "Integer"], "Integer", null), Entry("Fm", "fm", ["Integer", "Float", "Integer"], "Integer", "fastcall")), out var exit);
        Assert.Equal(0, exit);
        Assert.Equal("123\n30102\n", RunProgram());
    }

    [Theory]
    [InlineData("thiscall", "Float", "needs an Integer first parameter")]
    [InlineData("thiscall", null, "needs an Integer first parameter")]
    [InlineData("fastcall", "Integer,Integer,Integer", "decoration says 8 byte(s)")]
    [InlineData("stdcall", "Integer,Integer", "decoration says Fastcall")]
    public void RegisterConventionMistakesAreBuildErrors(string convention, string? parameters, string message)
    {
        Lib(new PeFixtureBuilder().WithCode([0xC3]).Export("plain", 0).Export("@fd@8", 0));
        var list = parameters is null ? [] : parameters.Split(',');
        var export = message.Contains("decoration") ? "fd" : "plain";
        var text = Cli("Print F(1, 2)\n", Catalog(Entry("F", export, list, "Integer", convention)), out var exit);
        Assert.Equal(1, exit);
        Assert.Contains(message, text);
    }
}
