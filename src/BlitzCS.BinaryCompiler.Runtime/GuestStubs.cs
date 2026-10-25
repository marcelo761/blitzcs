using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>The dynamic stub pool of a module is full (or was never compiled in), or a stub address was misused.</summary>
public sealed class GuestStubException(string message) : GuestException(message);

/// <summary>
/// Dynamic stub pool. A translated module can only call addresses that are translated-code dispatch targets, and the artifact has one import stub per real import.
/// When it was translated with <c>BinaryCompilerOptions.DynamicStubs = N</c> it carries N more import-style stubs (guest address ImageBase + ImportStubBaseRva + 16 * (imports + j)).
/// <see cref="AllocateStub"/> binds one of them to a managed handler at run time and returns the address the guest can store in a function pointer or a COM vtable;
/// <see cref="ReleaseStub"/> returns it to the pool. Every pool stub has one permanent reverse-P/Invoke thunk whose target is swapped atomically, so releasing a stub never frees
/// native code another guest thread may still be executing. A stub that is not allocated (never handed out, or released) FAULTS BY NAME AND INDEX when the guest calls it.
/// </summary>
public sealed unsafe partial class GuestModule
{
    private sealed class StubTarget(GuestImportHandler handler, uint popBytes, string label)
    {
        public readonly GuestImportHandler Handler = handler;
        public readonly uint PopBytes = popBytes;
        public readonly string Label = label;
    }

    private sealed class StubSlot
    {
        public volatile StubTarget Target = null!;
        public bool Allocated;
        public GuestImport Import = null!;
    }

    private StubSlot[] stubSlots = [];
    private readonly object stubGate = new();
    private SortedSet<int> freeStubs = [];

    /// <summary>Number of pool stubs the artifact carries (BinaryCompilerOptions.DynamicStubs at translation time).</summary>
    public int StubCapacity => artifact.DynamicStubs;

    /// <summary>Pool stubs currently bound to a handler.</summary>
    public int StubsInUse { get { lock (stubGate) return stubSlots.Length - freeStubs.Count; } }

    private uint StubAddress(int slot) => artifact.GuestImageBase + artifact.ImportStubBaseRva + 16u * (uint)(artifact.Imports.Count + slot);

    private StubTarget UnboundTarget(int slot, string state) =>
        new(_ => throw new GuestStubException($"The guest called dynamic stub #{slot} at 0x{StubAddress(slot):X8}, which is {state}."), 0, state);

    private void InitializeStubPool()
    {
        var n = artifact.DynamicStubs;
        stubSlots = new StubSlot[n];
        freeStubs = [];
        for (var j = 0; j < n; j++)
        {
            var slot = new StubSlot { Target = UnboundTarget(j, "not allocated (it was never handed out by AllocateStub)") };
            var index = j;
            // The thunk reads the slot's current target for every call; popBytes is therefore decided per call, not at creation.
            slot.Import = GuestImport.CreateIndirect(esp => { var t = slot.Target; return DispatchImport(t.Handler, t.PopBytes, esp); });
            stubSlots[index] = slot;
            freeStubs.Add(j);
            *(nint*)(hostMap + (nint)artifact.ImportThunkTableOffset + 8 * (artifact.Imports.Count + j)) = slot.Import.FunctionPointer;
        }
    }

    /// <summary>
    /// Binds a pool stub to <paramref name="handler"/> (stdcall when <paramref name="popBytes"/> &gt; 0: the stub pops that many argument bytes, a multiple of 4) and returns its guest address.
    /// Thread-safe. Throws <see cref="GuestStubException"/> when the pool is exhausted or the module was translated without one.
    /// </summary>
    public uint AllocateStub(GuestImportHandler handler, uint popBytes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (popBytes % 4 != 0) throw new ArgumentException("popBytes must be a multiple of 4.", nameof(popBytes));
        if (artifact.DynamicStubs == 0)
            throw new GuestStubException("This module was translated without a dynamic stub pool (BinaryCompilerOptions.DynamicStubs = 0), so a guest-callable function pointer cannot be created. Re-translate with DynamicStubs > 0 (the CLI does so for libraries that import LoadLibrary/GetProcAddress or are marked \"com\": true).");
        lock (stubGate)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(GuestModule));
            if (freeStubs.Count == 0)
                throw new GuestStubException($"The dynamic stub pool is exhausted: all {artifact.DynamicStubs} stubs are in use. Translate with a larger BinaryCompilerOptions.DynamicStubs (binaryLibraries \"dynamicStubs\" in the catalog) or release stubs.");
            var j = freeStubs.Min;
            freeStubs.Remove(j);
            var slot = stubSlots[j];
            slot.Allocated = true;
            slot.Target = new StubTarget(handler, popBytes, "allocated");
            return StubAddress(j);
        }
    }

    /// <summary>Returns a stub to the pool. A stub that is not allocated (including a second release) throws <see cref="GuestStubException"/>; a later guest call through the released address faults by name.</summary>
    public void ReleaseStub(uint address)
    {
        lock (stubGate)
        {
            if (!TryStubIndex(address, out var j)) throw new GuestStubException($"0x{address:X8} is not the address of a dynamic stub of this module.");
            var slot = stubSlots[j];
            if (!slot.Allocated) throw new GuestStubException($"Dynamic stub #{j} at 0x{address:X8} is not allocated (double release).");
            slot.Allocated = false;
            slot.Target = UnboundTarget(j, "released (the guest called a function pointer or interface that was already destroyed)");
            freeStubs.Add(j);
        }
    }

    /// <summary>True when <paramref name="address"/> is the exact address of a dynamic stub (allocated or not).</summary>
    public bool IsStubAddress(uint address) { lock (stubGate) return TryStubIndex(address, out _); }

    private bool TryStubIndex(uint address, out int slot)
    {
        slot = -1;
        var first = StubAddress(0);
        if (address < first || (address - first) % 16 != 0) return false;
        var j = (address - first) / 16;
        if (j >= (uint)stubSlots.Length) return false;
        slot = (int)j;
        return true;
    }
}
