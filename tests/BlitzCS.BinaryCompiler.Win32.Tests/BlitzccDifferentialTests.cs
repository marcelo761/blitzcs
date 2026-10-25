using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BlitzCS.BcRun;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// End-to-end differential: the ORIGINAL blitzcc.exe (genuine Blitz3D SDK V1.118, BLITZCS_BLITZ3D_DIR) compiled through BCSBRE must produce executables that are
/// byte-identical to the ones the same blitzcc.exe produces natively under Wine 11. The executable blitzcc writes is runtime.dll plus a patched header, and its
/// AddressOfEntryPoint is computed by linker.dll as GetProcAddress(runtime, name) - moduleBase, so the test fails if GetProcAddress hands out anything but the real address.
/// </summary>
[Collection("GuestRuntime")]
public sealed class BlitzccDifferentialTests : IDisposable
{
    // Produced by the original blitzcc.exe running natively under Wine 11 (Proton), deterministic across two runs, TimeDateStamp 0x66d235d0.
    private const string HelloSha256 = "d7d54f539e62c7a1a978156a9bfb773e689d0ba52eb9eb159b83d90d4733146d";
    private const string Prog2Sha256 = "099a287816166d186c52f675310e130cec73b49449f1ef0f59a75a9ba3d74659";
    private const string IncSha256 = "ff7e647967cb464c66460aeaa7a81acd596d334149cdffa0e990f7f9b4c000d4";

    private const string HelloSource = "Print \"hello\"\n";
    private const string IncMainSource = "Include \"inc_lib.bb\"\nPrint Twice(21)\n";
    private const string IncLibSource = "Function Twice%(v%)\n  Return v * 2\nEnd Function\n";
    private const string Prog2Source = """
        ; reference program 2
        Global total# = 0.0
        Global label$ = "ref"

        Type Item
          Field value%
          Field weight#
          Field name$
        End Type

        Function Scale%(x%, factor#)
          Local r% = x * factor
          Return r
        End Function

        Dim squares%(10)
        For i = 0 To 9
          squares(i) = i * i
        Next

        For k = 1 To 3
          it.Item = New Item
          it\value = Scale(k, 2.5)
          it\weight = Float(k) / 4.0
          it\name = label + Str(k)
          total = total + it\weight
        Next

        For it.Item = Each Item
          Print it\name + ":" + it\value + ":" + it\weight
          Delete it
        Next

        Print "total=" + total + " sq=" + squares(9)

        """;

    private readonly string dir = Directory.CreateTempSubdirectory("blitzccdiff-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>Compiles <paramref name="source"/> with the original blitzcc.exe in a writable copy of the SDK's bin directory and returns the produced executable.</summary>
    private byte[] Compile(string main, params (string Name, string Text)[] files)
    {
        var sdk = Blitz3DSdkFactAttribute.Dir!;
        var sandbox = Path.Combine(dir, "sandbox");
        if (!Directory.Exists(sandbox))
        {
            Directory.CreateDirectory(Path.Combine(sandbox, "bin"));
            foreach (var f in Directory.GetFiles(Path.Combine(sdk, "bin"))) File.Copy(f, Path.Combine(sandbox, "bin", Path.GetFileName(f)));
            foreach (var sub in new[] { "cfg", "userlibs" })
                if (Directory.Exists(Path.Combine(sdk, sub))) foreach (var f in Directory.GetFiles(Path.Combine(sdk, sub))) { Directory.CreateDirectory(Path.Combine(sandbox, sub)); File.Copy(f, Path.Combine(sandbox, sub, Path.GetFileName(f))); }
        }
        foreach (var (name, text) in files) File.WriteAllBytes(Path.Combine(sandbox, name), Encoding.ASCII.GetBytes(text));
        var output = Path.Combine(sandbox, "out.exe");
        File.Delete(output);
        var stdout = new MemoryStream();
        var log = new List<string>();
        var r = BcRunner.Run(new BcRunOptions
        {
            ExePath = Path.Combine(sandbox, "bin", "blitzcc.exe"), SandboxRoot = sandbox, AllowTranslationErrors = true,
            Args = ["-o", "C:\\out.exe", "C:\\" + main],
            Environment = new Dictionary<string, string> { ["blitzpath"] = "C:\\" },
            CacheDirectory = Path.Combine(dir, "cache"), Log = log.Add,
            Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream(),
        });
        var said = Encoding.Latin1.GetString(stdout.ToArray());
        Assert.True(r.Failure is null && r.ExitCode == 0 && File.Exists(output), $"blitzcc failed (exit {r.ExitCode}, {r.Failure}):\n{said}\n{string.Join("\n", r.Diagnostics)}");
        return File.ReadAllBytes(output);
    }

    private static void AssertDigest(string expected, byte[] actual, string what)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(actual));
        if (digest == expected) return;
        Assert.Fail($"{what}: SHA-256 {digest} differs from the native-Wine reference {expected} ({actual.Length} bytes). " +
                    "The reference bytes are not stored; the produced executable is runtime.dll plus a patched header, so inspect the region below. " + Describe(actual));
    }

    /// <summary>Names the PE region of the interesting offsets: the entry point field (the symptom of a wrong GetProcAddress) and, in general, where the file looks unusual.</summary>
    private static string Describe(byte[] pe)
    {
        if (pe.Length < 0x200 || pe[0] != 'M') return "the output is not a PE file.";
        var lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        var opt = lfanew + 24;
        var entry = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(opt + 16));
        var stamp = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(lfanew + 8));
        return $"PE header at 0x{lfanew:X}: IMAGE_OPTIONAL_HEADER.AddressOfEntryPoint (file offset 0x{opt + 16:X}) = 0x{entry:X8}" +
               (entry >= pe.Length * 16u ? " (not a plausible RVA: runtime.dll's entry RVA is computed as GetProcAddress - module base)" : "") + $", TimeDateStamp 0x{stamp:X8}.";
    }

    [Blitz3DSdkFact]
    public void HelloIsByteIdenticalToTheNativeWineReference() => AssertDigest(HelloSha256, Compile("hello.bb", ("hello.bb", HelloSource)), "hello.bb");

    [Blitz3DSdkFact]
    public void TypesFunctionsArraysAndFloatsAreByteIdenticalToTheNativeWineReference() => AssertDigest(Prog2Sha256, Compile("prog2.bb", ("prog2.bb", Prog2Source)), "prog2.bb");

    [Blitz3DSdkFact]
    public void IncludeIsByteIdenticalToTheNativeWineReference() =>
        AssertDigest(IncSha256, Compile("inc_main.bb", ("inc_main.bb", IncMainSource), ("inc_lib.bb", IncLibSource)), "inc_main.bb");

    /// <summary>Mutation check: a single flipped hex digit of the expected digest must fail the comparison, with the PE region in the message.</summary>
    [Blitz3DSdkFact]
    public void AFlippedExpectedDigitFailsAndNamesThePeRegion()
    {
        var exe = Compile("hello.bb", ("hello.bb", HelloSource));
        var flipped = (HelloSha256[0] == '0' ? "1" : "0") + HelloSha256[1..];
        var e = Assert.ThrowsAny<Exception>(() => AssertDigest(flipped, exe, "hello.bb"));
        Assert.Contains("AddressOfEntryPoint", e.Message);
        Assert.Contains("differs from the native-Wine reference", e.Message);
    }
}
