using System.Reflection.Emit;
using System.Runtime.InteropServices;
namespace BlitzCS.OriginalRuntime;

public static partial class Commands
{
    [ThreadStatic] private static Session? active;
    private static object? Call(string name, string returnType, string[] types, object[] arguments) =>
        (active ?? throw new InvalidOperationException("Run the program through the original-runtime x86 host.")).Call(name, returnType, types, arguments);

    /// <summary>Reads the real factory's registration table without executing a BASIC program.</summary>
    public static RuntimeRegistrationSnapshot Inspect(string runtimePath)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("Runtime inspection requires Windows .NET x86 (or Proton).");
        using var session = new Session(runtimePath);
        return new(session.Version, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runtimePath))).ToLowerInvariant(), session.Descriptors.AsReadOnly());
    }

    public static void Run(string runtimePath, Action program)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("The original Blitz3D DLL requires Windows .NET x86 (or Proton). The default BlitzCS target remains x64.");
        if (active is not null) throw new InvalidOperationException("Nested runtime execution is not supported.");
        using var session = new Session(runtimePath);
        active = session;
        try { session.Run(program); }
        finally { active = null; }
    }
    private sealed class Session : IDisposable
    {
        private readonly nint library;
        private readonly nint runtime;
        private readonly nint table;
        private readonly Dictionary<string, (nint Pointer, string Descriptor)> symbols = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Descriptors = new();
        internal int Version => Slot<VersionCall>(0)(runtime);
        private readonly Dictionary<string, Func<object[], object?>> calls = new(StringComparer.OrdinalIgnoreCase);
        private T Slot<T>(int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * 4));
        public Session(string path)
        {
            library = NativeLibrary.Load(Path.GetFullPath(path));
            var factory = Marshal.GetDelegateForFunctionPointer<Factory>(NativeLibrary.GetExport(library, "runtimeGetRuntime"));
            runtime = factory();
            if (runtime == 0) throw new InvalidOperationException("runtimeGetRuntime returned null.");
            table = Marshal.ReadIntPtr(runtime);
            var next = Slot<Next>(1); var value = Slot<Value>(2);
            while (true)
            {
                var text = next(runtime);
                if (text == 0) break;
                var descriptor = Marshal.PtrToStringAnsi(text)!;
                Descriptors.Add(descriptor);
                var start = descriptor[0] is '%' or '#' or '$' ? 1 : 0;
                var end = start;
                while (end < descriptor.Length && (char.IsLetterOrDigit(descriptor[end]) || descriptor[end] == '_')) end++;
                var pointer = value(runtime, text);
                if (pointer is 0 or -1) throw new InvalidOperationException("Invalid original runtime registration: " + descriptor);
                symbols[descriptor[start..end]] = ((nint)pointer, descriptor);
            }
            Console.Error.WriteLine($"Original Blitz3D runtime: version {Slot<VersionCall>(0)(runtime)}, {symbols.Count} registrations, {IntPtr.Size * 8}-bit process.");
            Slot<Startup>(3)(runtime, GetModuleHandle(null));
        }
        public void Run(Action program)
        {
            Exception? failure = null;
            bool entered = false;
            bool completed = false;
            var callback = new Entry(() => { entered = true; try { program(); completed = true; } catch (Exception e) { failure = e; } });
            Slot<Execute>(9)(runtime, callback, "", 0);
            GC.KeepAlive(callback);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (!entered) throw new InvalidOperationException("The original runtime failed to initialize; BASIC entry point was not reached.");
            if (!completed) throw new InvalidOperationException("The original runtime terminated before the managed BASIC entry point returned.");
        }
        public object? Call(string name, string result, string[] types, object[] arguments)
        {
            if (!symbols.TryGetValue(name, out var symbol)) throw new MissingMethodException("Original runtime has no registration for " + name);
            if (!calls.TryGetValue(name, out var call))
            {
                Validate(symbol.Descriptor, types, result);
                calls[name] = call = BuildCall(symbol.Pointer, types, result);
            }
            return call(arguments);
        }
        public void Dispose() { Slot<Shutdown>(4)(runtime); NativeLibrary.Free(library); }
    }
    static void Validate(string descriptor, string[] types, string result)
    {
        var actualResult = descriptor[0] switch { '%' => "Integer", '#' => "Float", '$' => "String", _ => "Void" };
        var actualTypes = new List<string>();
        // The numeric subset has no quoted string defaults containing type markers.
        for (var i = actualResult == "Void" ? 0 : 1; i < descriptor.Length; i++)
            if (descriptor[i] is '%' or '#' or '$') actualTypes.Add(descriptor[i] switch { '%' => "Integer", '#' => "Float", _ => "String" });
        if (actualResult != result || !actualTypes.SequenceEqual(types))
            throw new InvalidOperationException("Runtime registration signature disagrees with profile: " + descriptor);
    }
    static Func<object[], object?> BuildCall(nint pointer, string[] types, string result)
    {
        Type Clr(string type) => type switch { "Integer" => typeof(int), "Float" => typeof(float), "Void" => typeof(void), _ => throw new NotSupportedException(type) };
        var method = new DynamicMethod("OriginalRuntimeCall", typeof(object), [typeof(object[])]);
        var il = method.GetILGenerator();
        for (var i = 0; i < types.Length; i++) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Unbox_Any, Clr(types[i])); }
        il.Emit(OpCodes.Ldc_I4, pointer.ToInt32()); il.Emit(OpCodes.Conv_I);
        il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, Clr(result), types.Select(Clr).ToArray());
        if (result == "Void") il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Box, Clr(result));
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object[], object?>>();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Factory();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Entry();
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int VersionCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate nint Next(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int Value(nint self, nint symbol);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Startup(nint self, nint instance);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Shutdown(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Execute(nint self, Entry entry, [MarshalAs(UnmanagedType.LPStr)] string arguments, nint debugger);
}

public sealed record RuntimeRegistrationSnapshot(int Version, string RuntimeSHA256, IReadOnlyList<string> Descriptors);
