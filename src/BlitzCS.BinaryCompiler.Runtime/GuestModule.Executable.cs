namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Implemented by an exception that means "the guest ended its process" (the Win32 component's ExitProcess/TerminateProcess(self) exception). The runtime does not
/// reference the Win32 project: <see cref="GuestModule.RunExecutable"/> looks for this marker in the inner-exception chain of the <see cref="GuestImportException"/> that
/// reports a failed managed import and returns <see cref="ExitCode"/> instead of failing.
/// </summary>
public interface IGuestProcessExit
{
    /// <summary>The process exit code the guest passed.</summary>
    uint ExitCode { get; }
}

/// <summary>How a PE32 EXE ended (<see cref="GuestModule.RunExecutable"/>).</summary>
/// <param name="ExitCode">The code passed to ExitProcess, or EAX when the entry point returned.</param>
/// <param name="CalledExit">True when the guest ended through an <see cref="IGuestProcessExit"/> import; false when the entry point returned normally.</param>
public readonly record struct GuestExecutableResult(uint ExitCode, bool CalledExit);

public sealed unsafe partial class GuestModule
{
    /// <summary>True when the loaded image is an executable (not a DLL) with an entry point.</summary>
    public bool IsExecutable => !imageIsDll && imageEntryRva != 0;

    /// <summary>
    /// Runs the image's entry point as a process would start: a stdcall call with no arguments on a guest thread context (own TEB, fresh stack) that normally never
    /// returns because the C runtime ends in ExitProcess. An <see cref="IGuestProcessExit"/> raised by an import is the exit code; a CRT that returns instead
    /// yields EAX. Any other failure (translation trap, unbound import, memory fault) propagates unchanged. DLLs are refused: their entry point runs at load time as DllMain.
    /// The module stays loaded (heaps, handles, TLS) after the call; a second call re-runs the entry point on the same state, which no CRT supports.
    /// </summary>
    public GuestExecutableResult RunExecutable()
    {
        if (imageIsDll) throw new InvalidOperationException("RunExecutable needs an EXE image; a DLL's entry point runs as DllMain when it is loaded.");
        if (imageEntryRva == 0) throw new InvalidOperationException("The image has no entry point.");
        var target = ResolveEntry(imageEntryRva);
        EnterCall();
        try { return new GuestExecutableResult(InvokeCore(target, []), false); }
        catch (GuestImportException e) when (FindExit(e) is { } exit) { return new GuestExecutableResult(exit.ExitCode, true); }
        finally { ExitCall(); }
    }

    private static IGuestProcessExit? FindExit(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException!)
            if (x is IGuestProcessExit exit) return exit;
        return null;
    }
}
