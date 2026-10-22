using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class HandleCompilerTests
{
    [Theory]
    [InlineData("Type Node\nField value%\nEnd Type\nn.Node=New Node\nh%=Handle(n)\nx.Node=Object.Node(h)\nx\\value=42\nGlobal result=n\\value", 42)]
    [InlineData("Type Node\nEnd Type\nGlobal result=Handle(Null)", 0)]
    [InlineData("Type Node\nEnd Type\nn.Node=New Node\nGlobal result=Handle n", 1)]
    [InlineData("Type Node\nEnd Type\nn.Node=New Node\nx.Node=Object Node(Handle n)\nGlobal result=x=n", 1)]
    [InlineData("Type Node\nEnd Type\nn.Node=New Node\nh=Handle n\nDelete n\nGlobal result=Handle n", 0)]
    [InlineData("Type Node\nEnd Type\nn.Node=New Node\nh=Handle n\nx.Node=Object.Node(Str(h)+\"tail\")\nGlobal result=x=n", 1)]
    public void TypedHandlesPreserveIdentityAndLegacyConversions(string source, int expected) =>
        ArrayCompilerTests.WithAssembly(source, a => Assert.Equal(expected, a.GetType("BB")!.GetField("result")!.GetValue(null)));

    [Fact]
    public void IntegerAndObjectFieldsAndVectorFieldsPreserveHandleIdentityUntilDeletion()
    {
        ArrayCompilerTests.WithAssembly("""
            Type Node
              Field value%
            End Type
            Type Holder
              Field number%, reference.Node, references.Node[1]
            End Type
            holder.Holder=New Holder
            n.Node=New Node
            n\value=42
            holder\number=Handle(n)
            holder\reference=n
            holder\references[1]=n
            found.Node=Object.Node(holder\number)
            Global result%=found\value
            Global stable%=Handle(holder\references[1])=holder\number
            Delete n
            Global invalid%=Object.Node(holder\number)=Null
            Global aliases%=holder\reference=Null And holder\references[1]=Null
            """, assembly =>
        {
            var bb = assembly.GetType("BB")!;
            Assert.Equal(42, bb.GetField("result")!.GetValue(null));
            Assert.Equal(1, bb.GetField("stable")!.GetValue(null));
            Assert.Equal(1, bb.GetField("invalid")!.GetValue(null));
            Assert.Equal(1, bb.GetField("aliases")!.GetValue(null));
        });
    }

    [Theory]
    [InlineData("Print Handle(1)")]
    [InlineData("Print Handle(\"object\")")]
    [InlineData("n.Missing=Object.Missing(1)")]
    [InlineData("Type Node\nEnd Type\nn.Node=New Node\nx.Node=Object.Node(n)")]
    [InlineData("Type Node\nEnd Type\nGlobal v%[1]\nn.Node=Object.Node(v)")]
    [InlineData("Type Node\nEnd Type\nConst H=Handle(New Node)")]
    public void InvalidHandleOperationsProduceSourceDiagnostics(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "handles.bb"));
}
