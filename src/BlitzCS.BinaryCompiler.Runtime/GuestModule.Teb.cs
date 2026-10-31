using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Thread environment block support (docs/binary-teb-contract.md). Every OS thread owns one TEB page in low memory per guest process
/// (<see cref="GuestProcessEnvironment"/>: all modules of a <see cref="GuestModuleSet"/> share it), created at the thread's first guest call and
/// alive until the process environment is disposed (or the thread object is collected). The translated code reaches it through R15, which EnterGuest
/// loads from the call context, so nested calls on a thread, and code that jumps from one module into another, share one TEB while calls on different
/// threads never touch each other's; no lock is taken on the call path.
/// </summary>
public sealed unsafe partial class GuestModule
{
    internal const uint TebExceptionList = 0x00, TebStackBase = 0x04, TebStackLimit = 0x08, TebSelf = 0x18, TebProcessId = 0x20, TebThreadId = 0x24,
        TebTlsPointer = 0x2C, TebPeb = 0x30, TebLastError = 0x34, TebTlsSlots = 0xE10, TebTlsSlotCount = 64;
    internal const uint PebBeingDebugged = 0x02, PebImageBase = 0x08, PebLdr = 0x0C, PebProcessParameters = 0x10, PebProcessHeap = 0x18,
        PebNumberOfProcessors = 0x64, PebNtGlobalFlag = 0x68, PebOsMajor = 0xA4, PebOsMinor = 0xA8, PebOsBuild = 0xAC, PebOsCsdVersion = 0xAE, PebOsPlatformId = 0xB0;
    // RTL_USER_PROCESS_PARAMETERS (32-bit): offsets of the fields that are filled.
    internal const uint ParamsMaximumLength = 0x00, ParamsLength = 0x04, ParamsFlags = 0x08, ParamsStdInput = 0x18, ParamsStdOutput = 0x1C, ParamsStdError = 0x20,
        ParamsCurrentDirectory = 0x24, ParamsDllPath = 0x30, ParamsImagePathName = 0x38, ParamsCommandLine = 0x40, ParamsEnvironment = 0x48, ParamsSize = 0x290;
    private const uint RtlUserProcParamsNormalized = 1;
    private const uint DllProcessDetach = 0, DllProcessAttach = 1, DllThreadAttach = 2, DllThreadDetach = 3;

    private GuestProcessEnvironment env = null!;
    private int registrySlot = -1;
    private uint registryBound;
    private PeTls? tlsDirectory;
    private uint[] tlsCallbacks = [];
    private bool tlsProcessAttached;
    // True while loading and while disposing: threads first seen then do not get DLL_THREAD_ATTACH.
    private volatile bool initializing = true;

    /// <summary>Guest address of the PEB of the process this module belongs to (shared by all modules of a <see cref="GuestModuleSet"/>; 0 before the module is loaded).</summary>
    public uint PebAddress => env?.PebAddress ?? 0;

    /// <summary>Guest address of the TEB of the calling thread, or 0 when this thread has not called into the module yet. All modules of a set return the same address for a thread.</summary>
    public uint CurrentThreadTeb => threadState.IsValueCreated ? threadState.Value!.Teb?.Address ?? 0 : 0;

    private void AllocatePeb(PeImage image)
    {
        env.EnsurePeb(this, artifact.GuestImageBase, !image.IsDll, options.Process ?? GuestProcessInfo.Default);
        tlsDirectory = image.Tls;
    }

    /// <summary>
    /// Publishes the process heap handle in PEB+0x18 (ProcessHeap). The Win32 component calls it when it creates the default process heap, so a CRT reading the PEB
    /// and GetProcessHeap agree. The PEB reads 0 until then. Ignored for a disposed module.
    /// </summary>
    public void SetProcessHeap(uint handle) => env?.SetProcessHeap(handle);

    /// <summary>The calling thread's TEB (shared with every module of the process) and, for a thread new to this module, DLL_THREAD_ATTACH TLS callbacks.</summary>
    private void CreateThreadTeb(ThreadState state)
    {
        state.Teb = env.GetOrCreateTeb();
        if (!initializing && tlsProcessAttached) RunTlsCallbacks(DllThreadAttach);
    }

    private void RunTlsCallbacks(uint reason)
    {
        foreach (var rva in tlsCallbacks)
        {
            var target = ResolveEntry(rva - artifact.GuestImageBase);
            InvokeCore(target, [artifact.GuestImageBase, reason, 0u]);
        }
    }

    private void InitializeTls(PeImage image, byte[] mapped)
    {
        if (image.Tls is not { } tls) return;
        // The process hands out TLS indices in load order; the index is written before the image is protected. The data block of every thread
        // (existing and future) is created by the environment from the template in this module's image.
        var template = tls.EndRaw - tls.StartRaw;
        var index = env.RegisterTls(this, guestMap + (nint)(tls.StartRaw - artifact.GuestImageBase), template, (nuint)template + tls.SizeOfZeroFill);
        *(uint*)(guestMap + (nint)(tls.IndexAddress - artifact.GuestImageBase)) = (uint)index;
        var list = new List<uint>();
        if (tls.CallbacksVa != 0)
            for (var at = tls.CallbacksVa - artifact.GuestImageBase; (ulong)at + 4 <= (ulong)mapped.Length && list.Count < 64; at += 4)
            {
                var va = BitConverter.ToUInt32(mapped, (int)at);
                if (va == 0) break;
                list.Add(va);
            }
        tlsCallbacks = [.. list];
    }
}
