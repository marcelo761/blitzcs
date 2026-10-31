using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using BlitzCS.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BlitzCS.Compiler.Tests;

[Collection(nameof(StructPointerParameterTests))]
public sealed class StructPointerParameterTests
{
    private const string Header = """
        Type Pair
            Field a%, b#, c%
        End Type
        Global p.Pair = New Pair
        p\a = 7
        p\b = 1.5
        p\c = -3
        """;

    [Fact]
    public void ReflectionMapsSpanParametersToPointerDirections()
    {
        Assert.Equal([ExternalValueType.PointerIn], Bind(nameof(StructBindings.ReadIn)).Signature.ParameterTypes);
        Assert.Equal([ExternalValueType.PointerOut], Bind(nameof(StructBindings.FillOut)).Signature.ParameterTypes);
        Assert.Equal([ExternalValueType.PointerInOut, ExternalValueType.Integer], Bind(nameof(StructBindings.AddInOut)).Signature.ParameterTypes);
        Assert.Throws<ArgumentException>(() => Bind(nameof(InvalidStructBindings.OutInteger), typeof(InvalidStructBindings)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalFunctionSignature("P", ExternalValueType.PointerIn, []));
        Assert.Throws<ArgumentException>(() => new ExternalFunctionSignature("P", ExternalValueType.Void, [ExternalValueType.PointerIn], [0]));
    }

    [Fact]
    public void InPassesFieldImageWithoutCopyingBack()
    {
        Run(Header + "\nGlobal r% = ReadIn(p)", (assembly, _) =>
        {
            Assert.Equal(7, Global<int>(assembly, "r"));
            Assert.Equal([7, BitConverter.SingleToInt32Bits(1.5f), -3], StructBindings.LastSeen);
            Assert.Equal(7, Field<int>(assembly, "p", "a"));
        });
    }

    [Fact]
    public void OutZeroFillsAndCopiesBack()
    {
        Run(Header + "\nFillOut p", (assembly, _) =>
        {
            Assert.Equal([0, 0, 0], StructBindings.LastSeen);
            Assert.Equal(11, Field<int>(assembly, "p", "a"));
            Assert.Equal(2.25f, Field<float>(assembly, "p", "b"));
            Assert.Equal(33, Field<int>(assembly, "p", "c"));
        });
    }

    [Fact]
    public void InOutCopiesInAndBackInsideExpressionsAndSingleLineIf()
    {
        Run(Header + "\nGlobal r% = AddInOut(p, 10) + AddInOut(p, AddInOut(p, 1))\nIf r Then FillOut p : AddInOut(p, 100)", (assembly, _) =>
        {
            // Left to right: a = 17 (r1 = 17); inner a = 18 (returns 18); outer a = 36 (returns 36).
            Assert.Equal(17 + 36, Global<int>(assembly, "r"));
            Assert.Equal(111, Field<int>(assembly, "p", "a"));
            Assert.Equal(2.25f, Field<float>(assembly, "p", "b"));
        });
    }

    [Fact]
    public void NullPassesEmptySpanAndDeletedObjectsFail()
    {
        Run(Header + "\nGlobal r% = AddInOut(Null, 1)\nGlobal q.Pair = Null\nGlobal s% = ReadIn(q)", (assembly, _) =>
        {
            Assert.Equal(-1, Global<int>(assembly, "r"));
            Assert.Equal(-1, Global<int>(assembly, "s"));
        });
        Run(Header + "\nDelete p\nGlobal r% = ReadIn(p)", (_, error) =>
            Assert.Contains("Object does not exist", error!.Message), expectError: true);
    }

    [Theory]
    [InlineData("Global r% = ReadIn(5)", "requires a Type instance")]
    [InlineData("bank = 1\nGlobal r% = ReadIn(bank)", "including bank handles")]
    [InlineData("Global r% = ReadIn()", "is required")]
    [InlineData("Type S\nField name$\nEnd Type\ns.S = New S\nGlobal r% = ReadIn(s)", "field 'name' of Type 'S' is String")]
    [InlineData("Type V\nField x[2]\nEnd Type\nv.V = New V\nGlobal r% = ReadIn(v)", "field 'x' of Type 'V'")]
    [InlineData("Type O\nField o.O\nEnd Type\no.O = New O\nGlobal r% = ReadIn(o)", "field 'o' of Type 'O' is O")]
    [InlineData("Type E\nEnd Type\ne.E = New E\nGlobal r% = ReadIn(e)", "has no fields")]
    public void UnsupportedShapesAreSourceDiagnostics(string source, string message)
    {
        var error = Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "shape.bb", Registry()));
        Assert.True(error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), error.Message);
    }

    private static ExternalFunction Bind(string method, Type? owner = null) =>
        new(method, (owner ?? typeof(StructBindings)).GetMethod(method)!);

    private static ExternalFunctionRegistry Registry() => new([Bind(nameof(StructBindings.ReadIn)), Bind(nameof(StructBindings.FillOut)), Bind(nameof(StructBindings.AddInOut))]);

    private static T Global<T>(Assembly assembly, string name) =>
        (T)assembly.GetType("BB")!.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;

    private static T Field<T>(Assembly assembly, string owner, string field)
    {
        var value = Global<object>(assembly, owner);
        return (T)value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    }

    private static void Run(string source, Action<Assembly, Exception?> assert, bool expectError = false)
    {
        StructBindings.LastSeen = [];
        var registry = Registry();
        var code = BasicCompiler.Compile(source, "struct.bb", registry);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(registry.ReferencePaths).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(code)], refs,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + code);
        stream.Position = 0;
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            if (!expectError) { assembly.EntryPoint!.Invoke(null, null); assert(assembly, null); }
            else assert(assembly, Assert.Throws<TargetInvocationException>(() => assembly.EntryPoint!.Invoke(null, null)).InnerException);
        }
        finally { context.Unload(); }
    }
}

public static class StructBindings
{
    public static int[] LastSeen = [];

    private static void Record(ReadOnlySpan<byte> image)
    {
        LastSeen = new int[image.Length / 4];
        for (var i = 0; i < LastSeen.Length; i++) LastSeen[i] = BinaryPrimitives.ReadInt32LittleEndian(image[(i * 4)..]);
    }

    public static int ReadIn(ReadOnlySpan<byte> image)
    {
        if (image.IsEmpty) return -1;
        Record(image);
        return BinaryPrimitives.ReadInt32LittleEndian(image);
    }

    public static void FillOut([Out] Span<byte> image)
    {
        Record(image);
        BinaryPrimitives.WriteInt32LittleEndian(image, 11);
        BinaryPrimitives.WriteSingleLittleEndian(image[4..], 2.25f);
        BinaryPrimitives.WriteInt32LittleEndian(image[8..], 33);
    }

    public static int AddInOut(Span<byte> image, int amount)
    {
        if (image.IsEmpty) return -1;
        var value = BinaryPrimitives.ReadInt32LittleEndian(image) + amount;
        BinaryPrimitives.WriteInt32LittleEndian(image, value);
        return value;
    }
}

public static class InvalidStructBindings
{
    public static void OutInteger([Out] int value) { }
}
