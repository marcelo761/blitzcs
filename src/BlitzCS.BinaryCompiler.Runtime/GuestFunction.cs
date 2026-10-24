namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>A bound guest export. Calls are allocation-free and coordinated with module disposal.</summary>
public sealed class GuestFunction
{
    private readonly GuestModule module;
    private readonly nint hostTarget;

    internal GuestFunction(GuestModule module, string name, nint hostTarget, GuestCallingConvention convention, int argCount)
    {
        this.module = module;
        this.hostTarget = hostTarget;
        Name = name;
        Convention = convention;
        ArgCount = argCount;
    }

    public string Name { get; }
    public GuestCallingConvention Convention { get; }
    public int ArgCount { get; }

    /// <summary>Calls the export with exactly <see cref="ArgCount"/> 32-bit stack arguments and returns EAX.</summary>
    public uint Call(params ReadOnlySpan<uint> args)
    {
        if (args.Length != ArgCount)
            throw new ArgumentException($"Export '{Name}' was bound with {ArgCount} argument(s) but {args.Length} were supplied.", nameof(args));
        return module.Invoke(hostTarget, args);
    }
}
