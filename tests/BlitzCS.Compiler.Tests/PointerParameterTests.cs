using System.Buffers.Binary;
using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class PointerParameterTests
{
    [Fact]
    public void ExternalFunctionSignatureInitializesPointerParameters()
    {
        var sigDefault = new ExternalFunctionSignature("Test", ExternalValueType.Integer, [ExternalValueType.Integer, ExternalValueType.Float]);
        Assert.Equal(2, sigDefault.PointerParameters.Count);
        Assert.False(sigDefault.PointerParameters[0]);
        Assert.False(sigDefault.PointerParameters[1]);

        var sigExplicit = new ExternalFunctionSignature("Test2", ExternalValueType.Integer,
            [ExternalValueType.Integer, ExternalValueType.Float], null, [true, false]);
        Assert.True(sigExplicit.PointerParameters[0]);
        Assert.False(sigExplicit.PointerParameters[1]);
    }

    [Fact]
    public void ExternalFunctionSignatureValidatesPointerParameters()
    {
        // Length mismatch
        Assert.Throws<ArgumentException>(() => new ExternalFunctionSignature("Test", ExternalValueType.Integer,
            [ExternalValueType.Integer], null, [true, false]));

        // Pointer parameter on non-integer
        Assert.Throws<ArgumentException>(() => new ExternalFunctionSignature("Test", ExternalValueType.Integer,
            [ExternalValueType.Float], null, [true]));
        Assert.Throws<ArgumentException>(() => new ExternalFunctionSignature("Test", ExternalValueType.Integer,
            [ExternalValueType.String], null, [true]));
    }

    [Fact]
    public void ExternalBindingMarshalsObjectAndNullForPointerParameters()
    {
        var binding = new ExternalFunction("PassPtr", typeof(ExternalPtrMethods).GetMethod(nameof(ExternalPtrMethods.PassPtr))!);
        var sig = new ExternalFunctionSignature("PassPtr", ExternalValueType.Void,
            [ExternalValueType.Integer], null, [true]);
        var registry = ExternalFunctionRegistry.FromDeclarations([sig], [binding]);

        var code = """
            Type MyStruct
                Field a%, b#
            End Type
            s.MyStruct = New MyStruct
            s\a = 42
            s\b = 3.14
            PassPtr(s)
            PassPtr(Null)
            """;

        var csharp = BasicCompiler.Compile(code, "test.bb", registry);
        Assert.Contains("global::__BlitzObject.Marshal(v0", csharp);
        Assert.Contains("PassPtr(0)", csharp);
    }

    [Fact]
    public void EmitsPackAndUnpackForBlittableStructTypes()
    {
        var code = """
            Type MixedStruct
                Field x%, y#, z%
            End Type
            s.MixedStruct = New MixedStruct
            s\x = 100
            s\y = 2.5
            s\z = 200
            """;

        var csharp = BasicCompiler.Compile(code, "test.bb");
        Assert.Contains("internal override byte[] __PackFields()", csharp);
        Assert.Contains("internal override void __UnpackFields(byte[] __b)", csharp);
        Assert.Contains("WriteInt32LittleEndian", csharp);
        Assert.Contains("WriteSingleLittleEndian", csharp);
        Assert.Contains("ReadInt32LittleEndian", csharp);
        Assert.Contains("ReadSingleLittleEndian", csharp);
    }

    [Fact]
    public void DoesNotEmitPackAndUnpackForStructsWithStringFields()
    {
        var code = """
            Type StringStruct
                Field id%, name$
            End Type
            s.StringStruct = New StringStruct
            """;

        var csharp = BasicCompiler.Compile(code, "test.bb");
        Assert.DoesNotContain("override byte[] __PackFields()", csharp);
    }

    [Fact]
    public void RuntimePackUnpackAndMarshalWorkCorrectly()
    {
        var code = """
            Type Vec3
                Field x%, y#, z%
            End Type
            Global v.Vec3 = New Vec3
            v\x = 10
            v\y = 3.5
            v\z = 30
            """;

        ArrayCompilerTests.WithAssembly(code, assembly =>
        {
            var bb = assembly.GetType("BB")!;
            var v = bb.GetField("v", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            var blitzObjType = assembly.GetType("__BlitzObject")!;
            var packMethod = v.GetType().GetMethod("__PackFields", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var unpackMethod = v.GetType().GetMethod("__UnpackFields", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var marshalMethod = blitzObjType.GetMethod("Marshal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

            // Test packing
            var bytes = (byte[])packMethod.Invoke(v, null)!;
            Assert.Equal(12, bytes.Length);
            Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
            Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4)));
            Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));

            // Test unpacking modification
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 999);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), 7.25f);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 888);
            unpackMethod.Invoke(v, [bytes]);

            var xField = v.GetType().GetField("x", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var yField = v.GetType().GetField("y", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var zField = v.GetType().GetField("z", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal(999, (int)xField.GetValue(v)!);
            Assert.Equal(7.25f, (float)yField.GetValue(v)!);
            Assert.Equal(888, (int)zField.GetValue(v)!);

            // Test Marshal method
            var id = (int)marshalMethod.Invoke(null, [v, "test.bb(10,1)"])!;
            Assert.True(id < 0);
            var table = (System.Collections.Concurrent.ConcurrentDictionary<int, (byte[] Bytes, System.Action<byte[]> WriteBack)>)
                System.AppContext.GetData("__BlitzCS.ObjectBuffers")!;
            Assert.True(table.TryGetValue(id, out var entry));
            Assert.Equal(12, entry.Bytes.Length);
            Assert.Equal(999, BinaryPrimitives.ReadInt32LittleEndian(entry.Bytes.AsSpan(0, 4)));

            // Test writeback callback
            BinaryPrimitives.WriteInt32LittleEndian(entry.Bytes.AsSpan(0, 4), 1234);
            entry.WriteBack(entry.Bytes);
            Assert.Equal(1234, (int)xField.GetValue(v)!);
        });
    }
}

public static class ExternalPtrMethods
{
    public static void PassPtr(int ptr) { }
}
