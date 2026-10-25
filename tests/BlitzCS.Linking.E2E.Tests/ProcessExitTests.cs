using System.Diagnostics;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.Linking.E2E.Tests;

/// <summary>
/// A library that starts a worker thread which never leaves the module (FMOD's mixer is one, until FSOUND_Close) must not keep the program from ending: Windows ends the other
/// threads of an exiting process, so the runtime may not wait for them when it detaches the module at exit. Before the fix the exit waited forever for the thread's call to finish.
/// </summary>
[Collection("LinkingE2E")]
public sealed class ProcessExitTests : E2EBase
{
    private const uint LoopOffset = 0x80;

    private static byte[] Library()
    {
        var b = new PeFixtureBuilder();
        var create = PeFixtureBuilder.ImageBase + b.Import("kernel32.dll", "CreateThread");
        var sleep = PeFixtureBuilder.ImageBase + b.Import("kernel32.dll", "Sleep");
        var loop = (int)(PeFixtureBuilder.ImageBase + PeFixtureBuilder.TextRva + LoopOffset);
        // cdecl int spawn(): return CreateThread(NULL, 0, loop, NULL, 0, NULL);
        var spawn = TranslatorHarness.Asm(a =>
        {
            a.push(0); a.push(0); a.push(0); a.push(loop); a.push(0); a.push(0);
            a.call(__dword_ptr[(int)create]);
            a.ret();
        });
        // stdcall DWORD loop(LPVOID): for (;;) Sleep(5);
        var forever = TranslatorHarness.Asm(a =>
        {
            var top = a.CreateLabel();
            a.Label(ref top);
            a.push(5);
            a.call(__dword_ptr[(int)sleep]);
            a.jmp(top);
        });
        var pad = Enumerable.Repeat((byte)0x90, (int)LoopOffset - spawn.Length);
        return b.WithCode(spawn.Concat(pad).Concat(forever).ToArray()).Export("spawn", 0).Build();
    }

    [Fact]
    public async Task AProgramEndsEvenWhileALibraryThreadIsStillInsideTheModule()
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), Library());
        const string source = "Print Spawn() <> 0\nCS::Clock.Sleep(400)\nPrint \"main ends\"\n"
            + "<?cs module\npublic static class Clock { public static int Sleep(int ms) { System.Threading.Thread.Sleep(ms); return 0; } }\n?>\n";
        var catalog = "{\"binaryLibraries\":[{\"path\":\"lib.dll\",\"win32\":true}],\"functions\":[" + Entry("Spawn", [], "Integer", "lib.dll", "spawn", "cdecl") + "]}";
        var stdout = Build(source, catalog, out var exit, out var err);
        Assert.True(exit == 0, err + stdout);

        using var process = Process.Start(GeneratedProgramRunner.Create(Path.Combine(Root, "out", "main.dll")))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the program did not end: process exit waits for a library thread that never returns");
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("1\nmain ends\n", (await output).Replace("\r\n", "\n"));
        Assert.DoesNotContain("Unhandled", await error);
    }
}
