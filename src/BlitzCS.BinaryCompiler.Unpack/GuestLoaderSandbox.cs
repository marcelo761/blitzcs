using System.Text;

namespace BlitzCS.BinaryCompiler.Unpack;

/// <summary>One import the stub resolved (or had in its own import table): <see cref="Address"/> is the sandbox thunk handed to it.</summary>
public sealed record ResolvedImport(string Dll, string Name, uint Address, bool ViaGetProcAddress, bool Called);

/// <summary>
/// The bounded "Windows" the stub sees: a loader (LoadLibrary/GetModuleHandle/GetProcAddress), an allocator (VirtualAlloc/VirtualProtect/VirtualFree) and nothing else.
/// Every (dll, name) the stub resolves becomes a unique, 16-byte aligned thunk address in a reserved zone; reaching a thunk that is not a modelled loader API
/// refuses with <c>UNPACK_UNIMPLEMENTED_IMPORT_CALLED dll!name</c>. Resolving is therefore free, calling is explicit: unresolved never means silently successful.
/// </summary>
internal sealed class GuestLoaderSandbox
{
    public const uint ThunkBase = 0x7D000000, ThunkLimit = 0x100000, ExitAddress = 0x7DFFFFF0;
    private const uint ModuleBase = 0x7C000000, HeapBase = 0x40000000, HeapAlign = 0x10000;

    private readonly SandboxMemory mem;
    private readonly uint imageBase;
    private readonly List<ResolvedImport> imports = [];
    private readonly Dictionary<string, int> importIndex = new(StringComparer.Ordinal);
    private readonly List<(string Dll, uint Handle)> modules = [];
    private readonly List<string> notes = [];
    private uint nextHeap = HeapBase;
    private uint lastError;
    private static readonly HashSet<string> SystemDlls = ["kernel32.dll", "ntdll.dll", "user32.dll", "gdi32.dll", "advapi32.dll", "msvcrt.dll"];

    public GuestLoaderSandbox(SandboxMemory memory, uint imageBase)
    {
        mem = memory;
        this.imageBase = imageBase;
    }

    public IReadOnlyList<ResolvedImport> Imports => imports;
    public IReadOnlyList<string> Notes => notes;
    public int HeapAllocations { get; private set; }

    public static bool IsThunk(uint address) => address - ThunkBase < ThunkLimit;

    public static string NormalizeDll(string dll)
    {
        var d = dll.Trim().ToLowerInvariant();
        return d.EndsWith(".dll", StringComparison.Ordinal) || d.Contains('.') ? d : d + ".dll";
    }

    public uint Thunk(string dll, string name, bool viaGetProcAddress)
    {
        var key = NormalizeDll(dll) + "!" + name;
        if (importIndex.TryGetValue(key, out var i)) return ThunkBase + 16u * (uint)i;
        i = imports.Count;
        if (16L * (i + 1) >= ThunkLimit - 16) throw new UnpackRefusalException("UNPACK_TOO_MANY_IMPORTS", "The stub resolved an implausible number of imports.");
        imports.Add(new ResolvedImport(NormalizeDll(dll), name, ThunkBase + 16u * (uint)i, viaGetProcAddress, false));
        importIndex[key] = i;
        return ThunkBase + 16u * (uint)i;
    }

    private uint Module(string dll)
    {
        var d = NormalizeDll(dll);
        foreach (var (n, h) in modules) if (n == d) return h;
        var handle = ModuleBase + 0x10000u * (uint)(modules.Count + 1);
        modules.Add((d, handle));
        return handle;
    }

    /// <summary>Called by the run loop when EIP lies in the thunk zone: performs the modelled API (stdcall) and returns to the caller.</summary>
    public void CallHost(IA32Emulator cpu)
    {
        var idx = (int)((cpu.Eip - ThunkBase) / 16);
        if (cpu.Eip % 16 != 0 || idx >= imports.Count)
            throw new UnpackRefusalException("UNPACK_BAD_THUNK", $"Control reached 0x{cpu.Eip:X8} inside the import thunk zone, which is not a handed-out import address.", cpu.Eip);
        var imp = imports[idx];
        imports[idx] = imp with { Called = true };
        uint Arg(int k) => mem.Read(cpu.Esp + 4 + 4u * (uint)k, 4);
        void Done(uint eax, int argc) { cpu.Eax = eax; var ret = cpu.Pop(); cpu.Esp += 4u * (uint)argc; cpu.Eip = ret; }
        var label = $"{imp.Dll}!{imp.Name}";
        switch (label)
        {
            case "kernel32.dll!LoadLibraryA":
            {
                var name = mem.ReadCString(Arg(0)) ?? throw new UnpackRefusalException("UNPACK_BAD_ARGUMENT", "LoadLibraryA received an unreadable name.");
                notes.Add($"LoadLibraryA(\"{name}\")");
                Done(Module(name), 1); return;
            }
            case "kernel32.dll!GetModuleHandleA":
            {
                var p = Arg(0);
                if (p == 0) { Done(imageBase, 1); return; }
                var name = mem.ReadCString(p) ?? throw new UnpackRefusalException("UNPACK_BAD_ARGUMENT", "GetModuleHandleA received an unreadable name.");
                Done(SystemDlls.Contains(NormalizeDll(name)) ? Module(name) : 0, 1); return;
            }
            case "kernel32.dll!GetProcAddress":
            {
                var h = Arg(0); var np = Arg(1);
                var dll = modules.FirstOrDefault(m => m.Handle == h).Dll;
                if (dll is null)
                {
                    notes.Add($"GetProcAddress on handle 0x{h:X8} (image or unknown module) answered NULL");
                    Done(0, 2); return;
                }
                string name;
                if (np < 0x10000) name = "#" + np;
                else name = mem.ReadCString(np) ?? throw new UnpackRefusalException("UNPACK_BAD_ARGUMENT", "GetProcAddress received an unreadable name.");
                Done(Thunk(dll, name, true), 2); return;
            }
            case "kernel32.dll!VirtualAlloc":
            {
                var want = Arg(0); var size = Arg(1);
                if (size == 0 || size > 0x10000000) throw new UnpackRefusalException("UNPACK_BAD_ARGUMENT", $"VirtualAlloc of {size} byte(s) is refused.");
                var len = (size + 0xFFFFu) & ~0xFFFFu;
                var at = want != 0 && !mem.Overlaps(want & ~0xFFFFu, len) ? want & ~0xFFFFu : nextHeap;
                if (at == nextHeap) nextHeap += len;
                if (mem.Overlaps(at, len)) throw new UnpackRefusalException("UNPACK_ADDRESS_CONFLICT", "VirtualAlloc could not find free sandbox address space.");
                mem.Add(new SandboxRegion("heap@" + at.ToString("X8"), at, new byte[len], true));
                HeapAllocations++;
                Done(at, 4); return;
            }
            case "kernel32.dll!VirtualProtect":
            {
                var old = Arg(3);
                if (old != 0) mem.Write(old, 4, 0x40);
                Done(1, 4); return;
            }
            case "kernel32.dll!VirtualFree": Done(1, 3); return;
            case "kernel32.dll!FlushInstructionCache": Done(1, 3); return;
            case "kernel32.dll!GetCurrentProcess": Done(0xFFFFFFFF, 0); return;
            case "kernel32.dll!GetLastError": Done(lastError, 0); return;
            case "kernel32.dll!SetLastError": lastError = Arg(0); Done(0, 1); return;
            case "kernel32.dll!IsDebuggerPresent":
                throw new UnpackRefusalException("UNPACK_ANTI_DEBUG", "The stub calls IsDebuggerPresent; anti-debug probes are refused rather than answered.", cpu.Eip);
            case "kernel32.dll!ExitProcess": case "kernel32.dll!TerminateProcess":
                throw new UnpackRefusalException("UNPACK_STUB_EXITED", $"The stub called {label} before reaching an original entry point.", cpu.Eip);
            default:
                throw new UnpackRefusalException("UNPACK_UNIMPLEMENTED_IMPORT_CALLED",
                    $"The stub called {label}, which the unpack sandbox does not implement (it only models the loader and allocator APIs). Resolving an import is free; calling it is refused.", cpu.Eip);
        }
    }
}
