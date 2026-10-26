using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BlitzCS.BcRun;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class Blitz3DSdkDirectDrawFactAttribute : FactAttribute
{
    public Blitz3DSdkDirectDrawFactAttribute()
    {
        if (Blitz3DSdkFactAttribute.Dir is not { } dir) Skip = $"{Blitz3DSdkFactAttribute.Variable} is not set (point it at the extracted Blitz3D SDK root, the directory that contains bin/blitzcc.exe)";
        else if (!File.Exists(Path.Combine(dir, "bin", "blitzcc.exe"))) Skip = $"{Blitz3DSdkFactAttribute.Variable}='{dir}' has no bin/blitzcc.exe";
        else if (!OperatingSystem.IsWindows()) Skip = "needs a Windows host (Windows or Wine) with ddraw.dll; this host is not Windows";
        else if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("ddraw.dll", out _)) Skip = "the host's ddraw.dll cannot be loaded (under Wine: a prefix with the vkd3d DLLs, BLITZCS_WINE_GRAPHICS=1)";
    }
}

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

    // Compile errors: stdout (CRLF line ends) and exit code of the original blitzcc.exe -c under Wine 11 (Proton), dedicated prefix; Wine's own "ntsync" stderr line is not stdout.
    // blitzcc reports these errors with a C++ throw from deep in the parser/semantic phase that its main catches: guest SEH across guest frames (docs/binary-seh.md).
    private const string JoyUDirNative =
        "BlitzCC V1.118\r\n(C)opyright 2000-2003 Blitz Research Ltd\r\nCompiling \"C:\\help\\commands\\2d_examples\\JoyUDir.bb\"\r\nParsing...\r\n" +
        "\"C:\\help\\commands\\2d_examples\\JoyUDir.bb\":2:1:2:1:Expecting end-of-file\r\n";
    private const string StartNetGameNative =
        "BlitzCC V1.118\r\n(C)opyright 2000-2003 Blitz Research Ltd\r\nCompiling \"C:\\help\\commands\\2d_examples\\StartNetGame.bb\"\r\nParsing...\r\nGenerating...\r\n" +
        "\"C:\\help\\commands\\2d_examples\\StartNetGame.bb\":1:1:1:1:Function 'startnetgame' not found\r\n";
    // blitzcc ends with exit(-1): ExitProcess(0xFFFFFFFF). The native run's process status under Wine is 255 (the low byte, as POSIX reports it), and so is BcRun's.
    private const uint CompileErrorExitCode = 0xFFFFFFFF;
    private const int NativeWineProcessStatus = 255;

    private readonly string dir = Directory.CreateTempSubdirectory("blitzccdiff-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>Compiles <paramref name="source"/> with the original blitzcc.exe in a writable copy of the SDK's bin directory and returns the produced executable.</summary>
    private byte[] Compile(string main, params (string Name, string Text)[] files)
    {
        var sandbox = PrepareSandbox();
        foreach (var (name, text) in files) File.WriteAllBytes(Path.Combine(sandbox, name), Encoding.ASCII.GetBytes(text));
        var output = Path.Combine(sandbox, "out.exe");
        File.Delete(output);
        var (r, said, diagnostics) = RunBlitzcc(sandbox, ["-o", "C:\\out.exe", "C:\\" + main]);
        Assert.True(r.Failure is null && r.ExitCode == 0 && File.Exists(output), $"blitzcc failed (exit {r.ExitCode}, {r.Failure}):\n{said}\n{diagnostics}");
        return File.ReadAllBytes(output);
    }

    private (BcRunResult Result, string Stdout, string Diagnostics) RunBlitzcc(string sandbox, string[] args)
    {
        var stdout = new MemoryStream();
        var log = new List<string>();
        var r = BcRunner.Run(new BcRunOptions
        {
            ExePath = Path.Combine(sandbox, "bin", "blitzcc.exe"), SandboxRoot = sandbox, AllowTranslationErrors = true,
            Args = args,
            Environment = new Dictionary<string, string> { ["blitzpath"] = "C:\\" },
            CacheDirectory = Path.Combine(dir, "cache"), Log = log.Add,
            Stdout = stdout, Stderr = new MemoryStream(), Stdin = new MemoryStream(),
        });
        return (r, Encoding.Latin1.GetString(stdout.ToArray()), string.Join("\n", r.Diagnostics));
    }

    /// <summary>
    /// Compiles an SDK source that has a compile error with <c>-c</c>, at the same path (<c>C:\help\commands\2d_examples\...</c>) as the native reference run, and checks that
    /// the diagnostic and the exit code are exactly the native ones.
    /// </summary>
    private void AssertCompileError(string name, string expectedStdout)
    {
        var sandbox = PrepareSandbox();
        var rel = Path.Combine("help", "commands", "2d_examples", name);
        Directory.CreateDirectory(Path.Combine(sandbox, "help", "commands", "2d_examples"));
        File.Copy(Path.Combine(Blitz3DSdkFactAttribute.Dir!, rel), Path.Combine(sandbox, rel), overwrite: true);
        var (r, said, diagnostics) = RunBlitzcc(sandbox, ["-c", "C:\\help\\commands\\2d_examples\\" + name]);
        Assert.True(r.Failure is null, $"blitzcc did not end by itself: {r.Failure}\n{said}\n{diagnostics}");
        Assert.True(r.CalledExit, "blitzcc reports compile errors through exit(), i.e. ExitProcess");
        Assert.Equal(CompileErrorExitCode, r.ExitCode);
        Assert.Equal(NativeWineProcessStatus, (int)(r.ExitCode & 0xFF));
        Assert.Equal(expectedStdout, said);
    }

    private string PrepareSandbox()
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
        return sandbox;
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

    [Blitz3DSdkFact]
    public void ACompileErrorInTheParserPrintsTheNativeDiagnosticAndExitCode() => AssertCompileError("JoyUDir.bb", JoyUDirNative);

    [Blitz3DSdkFact]
    public void AnUnknownFunctionPrintsTheNativeDiagnosticAndExitCode() => AssertCompileError("StartNetGame.bb", StartNetGameNative);

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

    [Blitz3DSdkDirectDrawFact]
    public void ProducedGraphics3DExecutableRendersTexturedMeshReadsPixelsAndInputAndExitsZero()
    {
        const string source = """
            Graphics3D 320,240,0,2
            SetBuffer BackBuffer()
            cam = CreateCamera()
            CameraClsColor cam, 16, 32, 64
            light = CreateLight()
            cube = CreateCube()
            PositionEntity cube, 0, 0, 3
            EntityColor cube, 240, 80, 32
            EntityFX cube, 1
            tex = CreateTexture(16, 16)
            SetBuffer TextureBuffer(tex)
            ClsColor 255, 255, 255
            Cls
            SetBuffer BackBuffer()
            EntityTexture cube, tex
            For frame = 1 To 3
                TurnEntity cube, 0, 15, 0
                UpdateWorld
                RenderWorld
                k = KeyDown(1)
                mx = MouseX()
                my = MouseY()
                If frame = 3 Then
                    LockBuffer BackBuffer()
                    corner = ReadPixelFast(5, 5, BackBuffer()) And $FFFFFF
                    center = ReadPixelFast(160, 120, BackBuffer()) And $FFFFFF
                    UnlockBuffer BackBuffer()
                EndIf
                Flip
            Next
            f = WriteFile("C:\g3d_out.txt")
            WriteLine f, "corner=" + Hex$(corner)
            WriteLine f, "center=" + Hex$(center)
            WriteLine f, "key=" + k + " mx=" + mx + " my=" + my
            CloseFile f
            End
            """;

        Compile("g3d.bb", ("g3d.bb", source));
        var sandbox = PrepareSandbox();
        var exePath = Path.Combine(sandbox, "out.exe");
        var outFile = Path.Combine(sandbox, "g3d_out.txt");
        File.Delete(outFile);

        var r = BcRunner.Run(new BcRunOptions
        {
            ExePath = exePath, SandboxRoot = sandbox, TargetOs = "windows-x64",
            CacheDirectory = Path.Combine(dir, "cache"),
            Stdout = new MemoryStream(), Stderr = new MemoryStream(), Stdin = new MemoryStream(),
        });
        Assert.True(r.Failure is null, $"produced Graphics3D exe failed: {r.Failure}\n{string.Join("\n", r.Diagnostics)}");
        Assert.True(r.CalledExit);
        Assert.Equal(0u, r.ExitCode);
        Assert.True(File.Exists(outFile), "produced Graphics3D exe did not write C:\\g3d_out.txt");
        var report = File.ReadAllText(outFile);
        Assert.Contains("corner=00102040", report);
        Assert.Contains("center=00F05020", report);
        Assert.Contains("key=0 mx=160 my=120", report);
    }
}
