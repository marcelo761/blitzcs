namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Supplies the host services (for example the Win32 component) that a translated binary library imports. The runtime never depends on a
/// concrete environment: the generated bindings register one per library. <see cref="CreateResolver"/> is called once per module, so an environment
/// may own per-module state (heaps, handles, TLS); resolvers that implement <see cref="IDisposable"/> are disposed with the module set.
/// </summary>
public interface IBinaryHostEnvironment
{
    /// <summary>Creates the import resolver for the module called <paramref name="moduleName"/> (its file name, for example <c>ogg.dll</c>).</summary>
    IGuestImportResolver CreateResolver(string moduleName);
}

/// <summary>Composes environments: for every import the first environment that provides it wins, so a specific service set can be layered over a general one.</summary>
public sealed class CompositeBinaryHostEnvironment(params IBinaryHostEnvironment[] environments) : IBinaryHostEnvironment
{
    private readonly IBinaryHostEnvironment[] environments = [.. environments];

    public IGuestImportResolver CreateResolver(string moduleName)
    {
        var created = new List<IGuestImportResolver>();
        try
        {
            foreach (var environment in environments) created.Add(environment.CreateResolver(moduleName));
            return new CompositeResolver([.. created]);
        }
        catch
        {
            foreach (var resolver in created) (resolver as IDisposable)?.Dispose();
            throw;
        }
    }

    private sealed class CompositeResolver(IGuestImportResolver[] resolvers) : IGuestImportResolver, IDisposable
    {
        private readonly ChainedGuestImportResolver chain = new(resolvers);
        public GuestImport? Resolve(string dll, string? name, ushort? ordinal) => chain.Resolve(dll, name, ordinal);
        public void Dispose() { foreach (var resolver in resolvers) (resolver as IDisposable)?.Dispose(); }
    }
}

/// <summary>Per-library settings of a <see cref="BinaryLibrary"/> that the generated bindings pass from the catalog.</summary>
public sealed class BinaryLibraryOptions
{
    /// <summary>Host services for this library; null means imports can only come from sibling libraries.</summary>
    public IBinaryHostEnvironment? Environment { get; init; }

    /// <summary>
    /// True: create the environment's resolver for the module always. False: only when the module imports from a DLL that is not a registered sibling
    /// (so a self-contained library does not allocate an emulated process).
    /// </summary>
    public bool EnvironmentRequired { get; init; }

    /// <summary>Run DllMain(DLL_PROCESS_ATTACH) when the module loads (CRT initialisation). Default true.</summary>
    public bool RunEntryPoint { get; init; } = true;

    /// <summary>Bind imports nobody provides to a stub that faults naming <c>dll!name</c> when called, instead of failing the load.</summary>
    public bool AllowUnboundImports { get; init; }

    /// <summary>How other libraries of the process call this library's exports (needed when a sibling imports an undecorated export).</summary>
    public ExportConventions? Conventions { get; init; }
}
