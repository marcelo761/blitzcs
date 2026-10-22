using BlitzCS.Compiler;
using Xunit;
namespace BlitzCS.Compiler.Tests;

public class ObjectCompilerTests
{
    [Fact]
    public void ObjectsHaveTypedFieldsIdentityAndOrderedLists()
    {
        ArrayCompilerTests.WithAssembly("""
Type Person
 Field age%, name$, friend.Person
End Type
Local a.Person=New Person
Local b.Person=New Person
a\age=40
a\name="Ada"
a\friend=b
b\age=2
Global result=a\age+a\friend\age
If First Person<>a Then result=0
If Last Person<>b Then result=0
If After a<>b Then result=0
If Before b<>a Then result=0
""", a => Assert.Equal(42, a.GetType("BB")!.GetField("result")!.GetValue(null)));
    }
    [Fact]
    public void ForwardObjectFieldsAndPrimitiveBridgeWrappersExecute()
    {
        ArrayCompilerTests.WithAssembly("""
Type Parent
 Field child.Child
End Type
Type Child
 Field parent.Parent, value#
End Type
Global owner.Parent=New Parent
owner\child=New Child
owner\child\parent=owner
owner\child\value=41.6
Global result=CS::Api.Answer()
Function ReadValue%()
Return owner\child\value
End Function
<?cs module public static class Api { public static int Answer() => BB.readvalue(); } ?>
""", a => Assert.Equal(42, a.GetType("BB")!.GetField("result")!.GetValue(null)));
    }
    [Fact]
    public void DeleteNullIsANoopWithoutAnyDeclaredType()
    {
        ArrayCompilerTests.WithAssembly("Delete Null\nGlobal result=42", a => Assert.Equal(42, a.GetType("BB")!.GetField("result")!.GetValue(null)));
    }

    [Fact]
    public void BeforeAndAfterTraverseFromRetainedDeletedAliases()
    {
        ArrayCompilerTests.WithAssembly("""
Type Person
 Field age%
End Type
Local a.Person=New Person
Local b.Person=New Person
Local c.Person=New Person
a\age=1
b\age=2
c\age=3
Delete b
Local following.Person=After b
Local preceding.Person=Before b
Global result=following\age*10+preceding\age
Delete b
If After a<>c Then result=0
""", a => Assert.Equal(31, a.GetType("BB")!.GetField("result")!.GetValue(null)));
    }

    [Fact]
    public void ScientificFloatStringConstantsMatchTheRuntimeDuringStepBinding()
    {
        ArrayCompilerTests.WithAssembly("Global result=0\nFor i=1 To 2 Step (Str(.0001)=\"1.e-004\")\nresult=result+1\nNext",
            a => Assert.Equal(2, a.GetType("BB")!.GetField("result")!.GetValue(null)));
    }

    [Theory]
    [InlineData("Local a.Person=New Person\nLocal b.Person=a\nDelete a\nGlobal result=(a=Null)+(b=Null)*10+(a=b)*100", 111)]
    [InlineData("Local a.Person=New Person\nLocal b.Person=New Person\nDelete a\nDelete b\nGlobal result=(a=b)+(First Person=Null)*10+(Last Person=Null)*100", 111)]
    [InlineData("Local a.Person=New Person\nLocal b.Person=New Person\na\\age=1\nb\\age=2\nInsert b Before a\nGlobal result=0\nFor p.Person=Each Person\nresult=result*10+p\\age\nNext", 21)]
    [InlineData("Local a.Person=New Person\nLocal b.Person=New Person\na\\age=1\nb\\age=2\nGlobal result=0\nFor p.Person=Each Person\nresult=result*10+p\\age\nDelete p\nNext\nresult=result+(First Person=Null)*100", 112)]
    [InlineData("Local a.Person=New Person\nLocal b.Person=New Person\nLocal c.Person=New Person\na\\age=1\nb\\age=2\nc\\age=3\nGlobal result=0\nFor p.Person=Each Person\nresult=result*10+p\\age\nIf p=a Then Delete b\nNext", 13)]
    [InlineData("Local a.Person=New Person\na\\age=1\nGlobal result=0\nFor p.Person=Each Person\nresult=result*10+p\\age\nIf p=a Then\nLocal b.Person=New Person\nb\\age=2\nEndIf\nNext", 12)]
    [InlineData("Local a.Person=New Person\nLocal b.Person=New Person\nGlobal result=0\nFor p.Person=Each Person\nFor q.Person=Each Person\nresult=result+1\nNext\nNext", 4)]
    [InlineData("Local a.Person=New Person\nDelete Each Person\nDelete a\nGlobal result=(a=Null)+(First Person=Null)*10", 11)]
    [InlineData("Local p.Person=Create()\nGlobal result=Age(p)\nFunction Create.Person()\nLocal p.Person=New Person\np\\age=42\nReturn p\nEnd Function\nFunction Age%(p.Person)\nReturn p\\age\nEnd Function", 42)]
    [InlineData("Local a.Person=New Person\na\\age=1\nDim people.Person(1)\nGlobal result=0, calls=0\nFor people(Slot())=Each Person\nresult=result+people(0)\\age\nNext\nresult=result+calls*10\nFunction Slot%()\ncalls=calls+1\nReturn 0\nEnd Function", 21)]
    [InlineData("Dim people.Person(1)\npeople(0)=New Person\npeople(0)\\age=42\nGlobal result=people(0)\\age", 42)]
    [InlineData("Local p.Person=New Person\nGlobal result=0\nFor p\\age=1 To 3\nresult=result+p\\age\nNext", 6)]
    [InlineData("Dim people.Person(1)\npeople(0)=New Person\nLocal p.Person=people(0)\np\\age=42\nGlobal result=people(0)\\age", 42)]
    public void ObjectLifetimeOrderingAndFunctionsExecute(string body, int expected)
    {
        ArrayCompilerTests.WithAssembly("Type Person\nField age%, peer.Person\nEnd Type\n" + body,
            a => Assert.Equal(expected, a.GetType("BB")!.GetField("result")!.GetValue(null)));
    }

    [Theory]
    [InlineData("Local p.Person=Null\nPrint p\\age")]
    [InlineData("Local p.Person=Null\nLocal q.Person=After p")]
    [InlineData("Local p.Person=New Person\nDelete p\np\\age=1")]
    [InlineData("Local p.Person=Null\nLocal q.Person=New Person\nInsert p Before q")]
    public void InvalidReferencesHaveBasicLocations(string body)
    {
        ArrayCompilerTests.WithAssembly("Type Person\nField age%\nEnd Type\n" + body, _ => {}, "objects.bb", error => {
            Assert.Contains("objects.bb(", error.Message);
            Assert.Contains("Object does not exist", error.Message);
        });
    }

    [Theory]
    [InlineData("Type A\nEnd Type\nType B\nEnd Type\nLocal x.A=New B")]
    [InlineData("Type A\nEnd Type\nLocal x.A=1")]
    [InlineData("Local x.Missing=Null")]
    [InlineData("Type A\nEnd Type\nLocal x.A=New A\nIf x Then Print 1")]
    [InlineData("Type A\nEnd Type\nLocal x.A=New A\nPrint x")]
    [InlineData("Type A\nEnd Type\nLocal x.A=New A\nPrint CS::System.Math.Abs(x)")]
    [InlineData("Type A\nField x.Missing\nEnd Type")]
    [InlineData("Type A\nField x%, x$\nEnd Type")]
    [InlineData("Type A\nEnd Type\nLocal x.A=New A\nPrint x\\missing")]
    [InlineData("Type A\nEnd Type\nLocal x.A=New A\nPrint x+x")]
    [InlineData("Type A\nEnd Type\nLocal x.A=New A\nPrint x<Null")]
    [InlineData("Type A\nEnd Type\nFor x=Each A\nNext")]
    [InlineData("Type A\nEnd Type\nLocal p.A=New A\nSelect p\nCase Null\nEnd Select")]
    [InlineData("Select Null\nCase Null\nEnd Select")]
    public void ObjectTypesAreNominalAndRequireDeclarations(string source) => Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source,"objects.bb"));
}
