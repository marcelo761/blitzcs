using System.Reflection.Emit;
using System.Runtime.InteropServices;
namespace BlitzCS.OriginalRuntime;

public static partial class Commands
{
    [ThreadStatic] private static Session? active;
    private static Session Current => active ?? throw new InvalidOperationException("Run the program through the original-runtime x86 host.");
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
        internal readonly nint[] Pointers = new nint[CommandNames.Length];
        internal readonly bool[] Traced = new bool[CommandNames.Length];
        private readonly nint library;
        private readonly nint runtime;
        private readonly nint table;
        private readonly Dictionary<string, (nint Pointer, string Descriptor)> symbols = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Descriptors = new();
        internal int Version => Slot<VersionCall>(0)(runtime);
        private readonly Dictionary<string, Func<object[], object?>> calls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> tracedCalls = new((Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_TRACE") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        private static readonly System.Text.Json.JsonSerializerOptions TraceOptions = new()
        { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        private T Slot<T>(int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * 4));
        public Session(string path)
        {
            for (int i = 0; i < CommandNames.Length; i++) Traced[i] = tracedCalls.Contains(CommandNames[i]);
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
        internal nint Resolve(int index, string name, string result, string[] types)
        {
            if (!symbols.TryGetValue(name, out var symbol)) throw new MissingMethodException("Original runtime has no registration for " + name);
            Validate(symbol.Descriptor, types, result);
            return Pointers[index] = symbol.Pointer;
        }
        public object? Call(string name, string result, string[] types, object[] arguments)
        {
            var trace = tracedCalls.Contains(name);
            if (trace) Console.Error.WriteLine($"Original call {name}: {System.Text.Json.JsonSerializer.Serialize(arguments, TraceOptions)}");
            if (trace && Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_TRACE_STACK") == "1")
                Console.Error.WriteLine(new System.Diagnostics.StackTrace(1, true));
            if (!symbols.TryGetValue(name, out var symbol)) throw new MissingMethodException("Original runtime has no registration for " + name);
            if (!calls.TryGetValue(name, out var call))
            {
                Validate(symbol.Descriptor, types, result);
                calls[name] = call = BuildCall(symbol.Pointer, types, result);
            }
            var native = (object[])arguments.Clone();
            var transferred = false;
            var strings = new List<nint>();
            try
            {
                for (var i = 0; i < types.Length; i++)
                    if (types[i] == "String") { var value = ToNativeString((string)arguments[i]); strings.Add(value); native[i] = value; }
                // Runtime commands take ownership of each BBStr argument, exactly as the original compiler does.
                transferred = true;
                var returned = call(native);
                var managedResult = result == "String" ? FromNativeString((nint)returned!) : returned;
                if (trace) Console.Error.WriteLine($"Original result {name}: {System.Text.Json.JsonSerializer.Serialize(managedResult, TraceOptions)}");
                return managedResult;
            }
            finally { if (!transferred) foreach (var value in strings) Internal<ReleaseString>("_bbStrRelease")(value); }
        }
        internal void Release(nint value) => Internal<ReleaseString>("_bbStrRelease")(value);
        private readonly Dictionary<string, Delegate> helpers = new(StringComparer.Ordinal);
        private T Internal<T>(string name) where T : Delegate
        {
            if (helpers.TryGetValue(name, out var cached)) return (T)cached;
            if (!symbols.TryGetValue(name, out var symbol)) throw new MissingMethodException("Original runtime has no registration for " + name);
            var helper = Marshal.GetDelegateForFunctionPointer<T>(symbol.Pointer);
            helpers.Add(name, helper);
            return helper;
        }
        private static readonly System.Text.Encoding LegacyEncoding = CreateEncoding();
        private static System.Text.Encoding CreateEncoding()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(1252, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
        }
        internal unsafe nint ToNativeString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var length = LegacyEncoding.GetByteCount(value);
            byte[]? rented = null;
            Span<byte> bytes = length < 512 ? stackalloc byte[length + 1]
                : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(checked(length + 1))).AsSpan(0, length + 1);
            nint result = 0;
            try
            {
                LegacyEncoding.GetBytes(value, bytes);
                bytes[length] = 0;
                fixed (byte* buffer = bytes)
                {
                    result = Internal<ConvertString>("_bbCStrToStr")((nint)buffer);
                    // Preserve embedded NULs through the original engine's string helpers.
                    for (var i = 0; i < length; i++)
                        if (bytes[i] == 0)
                        {
                            result = Internal<ConcatString>("_bbStrConcat")(result, Internal<CharacterString>("Chr")(0));
                            result = Internal<ConcatString>("_bbStrConcat")(result, Internal<ConvertString>("_bbCStrToStr")((nint)(buffer + i + 1)));
                        }
                }
                var owned = result; result = 0; return owned;
            }
            finally
            {
                if (result != 0) Release(result);
                if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
        internal unsafe string FromNativeString(nint value)
        {
            if (value == 0) throw new InvalidOperationException("Original runtime returned a null BBStr.");
            // Len consumes its argument; clone through the original helper, without inspecting C++ object layout.
            var clone = Internal<LoadString>("_bbStrLoad")(ref value);
            var length = Internal<StringLength>("Len")(clone);
            var text = Internal<ConvertString>("_bbStrToCStr")(value); // consumes the original BBStr
            return LegacyEncoding.GetString((byte*)text, length);
        }
        public void Dispose() { Slot<Shutdown>(4)(runtime); NativeLibrary.Free(library); }
    }
    static void Validate(string descriptor, string[] types, string result)
    {
        var actualResult = descriptor[0] switch { '%' => "Integer", '#' => "Float", '$' => "String", _ => "Void" };
        var actualTypes = new List<string>();
        var quoted = false;
        for (var i = actualResult == "Void" ? 0 : 1; i < descriptor.Length; i++)
        {
            if (descriptor[i] == '"') { quoted = !quoted; continue; }
            if (!quoted && descriptor[i] is '%' or '#' or '$') actualTypes.Add(descriptor[i] switch { '%' => "Integer", '#' => "Float", _ => "String" });
        }
        if (actualResult != result || !actualTypes.SequenceEqual(types))
            throw new InvalidOperationException("Runtime registration signature disagrees with profile: " + descriptor);
    }
    static Func<object[], object?> BuildCall(nint pointer, string[] types, string result)
    {
        Type Clr(string type) => type switch { "Integer" => typeof(int), "Float" => typeof(float), "Void" => typeof(void), "String" => typeof(nint), _ => throw new NotSupportedException(type) };
        var method = new DynamicMethod("OriginalRuntimeCall", typeof(object), [typeof(object[])]);
        var il = method.GetILGenerator();
        for (var i = 0; i < types.Length; i++) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Unbox_Any, Clr(types[i])); }
        il.Emit(OpCodes.Ldc_I4, pointer.ToInt32()); il.Emit(OpCodes.Conv_I);
        // Registered x86 commands clean their arguments (also true for the string helpers).
        // The factory and the managed entry callback have separate cdecl contracts.
        il.EmitCalli(OpCodes.Calli, CallingConvention.StdCall, Clr(result), types.Select(Clr).ToArray());
        if (result == "Void") il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Box, Clr(result));
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object[], object?>>();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Factory();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Entry();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate nint ConvertString(nint text);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate nint CharacterString(int character);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate nint ConcatString(nint left, nint right);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate nint LoadString(ref nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StringLength(nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ReleaseString(nint value);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int VersionCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate nint Next(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int Value(nint self, nint symbol);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Startup(nint self, nint instance);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Shutdown(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Execute(nint self, Entry entry, [MarshalAs(UnmanagedType.LPStr)] string arguments, nint debugger);
}

public sealed record RuntimeRegistrationSnapshot(int Version, string RuntimeSHA256, IReadOnlyList<string> Descriptors);
