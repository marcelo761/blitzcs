using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>The module-table half of kernel32 shared by the ANSI and wide entry points: emulated DLLs and host-registered modules first, then guest DLLs (<see cref="GuestLibraryLoader"/>).</summary>
internal static class LibraryApi
{
    public static uint Load(Win32Call c, string name)
    {
        if (name.Length == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if (c.Ctx.Process.Find(name) is { } module) return module.Handle;
        return c.Ctx.Options.GuestLibraries is { } libs ? libs.Load(c, name) : c.Ctx.Fail(Win32Error.ModNotFound);
    }

    public static uint ModuleHandle(Win32Call c, string? name)
    {
        if (name is null) return c.Ctx.Options.MainModuleBase;
        if (c.Ctx.Process.Find(name) is { } module) return module.Handle;
        return c.Ctx.Options.GuestLibraries?.FindLoaded(c, name)?.Handle ?? c.Ctx.Fail(Win32Error.ModNotFound);
    }

    public static uint Free(Win32Call c)
    {
        if (c.Ctx.Process.Find(c[0]) is not null) return 1;
        return c.Ctx.Options.GuestLibraries is { } libs ? libs.Free(c, c[0]) : c.Ctx.Fail(Win32Error.InvalidHandle);
    }

    public static uint ProcAddress(Win32Call c)
    {
        var byOrdinal = c[1] < 0x10000;
        var export = byOrdinal ? "#" + c[1] : c.Mem.ReadAnsiString(c[1], 1024);
        var module = c.Ctx.Process.Find(c[0]);
        if (module is null)
        {
            if (c.Ctx.Options.GuestLibraries is { } libs && libs.Find(c[0]) is not null)
                return libs.ProcAddress(c, c[0], byOrdinal ? null : export, (ushort)c[1]);
            return c.Ctx.Fail(Win32Error.ModNotFound);
        }
        if (module.Exports.TryGetValue(export, out var address)) return address;
        if (c.Ctx.Process.IsSystem(module) && c[1] >= 0x10000 && c.Ctx.Process.ExportStubAddress?.Invoke(module.Name, export) is { } stub) return stub;
        if (c.Ctx.Process.IsSystem(module) && c[1] >= 0x10000 && c.Ctx.ApiLookup?.Invoke(module.Name, export) is { } api)
        {
            // An emulated export becomes a guest-callable function pointer: a dynamic stub of the running module bound to the same handler as the import.
            var guestModule = GuestModule.Current;
            if (guestModule is not null && guestModule.StubCapacity > 0) return c.Ctx.Process.StubFor(guestModule, api);
            c.Ctx.Options.DebugOutput?.Invoke($"GetProcAddress({module.Name}!{export}): the function exists but this module was translated without a dynamic stub pool, so it has no callable address (re-translate with DynamicStubs > 0).");
        }
        return c.Ctx.Fail(Win32Error.ProcNotFound);
    }
}
