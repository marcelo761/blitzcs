using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class DependencyAnalysisTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-dependencies-" + Guid.NewGuid());

    public DependencyAnalysisTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void KeepsEveryExternalSiteWithLocationShapeAndLexicalOrder()
    {
        var path = Path.Combine(directory, "main.bb");
        var calls = BasicDependencyAnalysis.Analyze("Draw 1, Fetch(2)\nx=Fetch#()\nDraw(3)\n", path);

        Assert.Collection(calls,
            call => Assert.Equal(new DependencyCallSite(path, 1, 1, "draw", 2, true), call),
            call => Assert.Equal(new DependencyCallSite(path, 1, 9, "fetch", 1, false), call),
            call => Assert.Equal(new DependencyCallSite(path, 2, 3, "fetch", 0, false), call),
            call => Assert.Equal(new DependencyCallSite(path, 3, 1, "draw", 1, true), call));
    }

    [Fact]
    public void IncludesShareFunctionsAndArraysAndPreserveExpansionOrder()
    {
        var child = Path.Combine(directory, "child.bb");
        File.WriteAllText(child, "Dim values%(3)\nChildCall\nFunction LocalFn#(x%=DefaultValue())\nReturn InnerCall(x)\nEnd Function\n");
        var source = "BeforeCall\nInclude \"child.bb\"\nInclude \"CHILD.BB\"\nLocalFn%(values(IndexCall()))\nAfterCall\n";
        var path = Path.Combine(directory, "main.bb");

        var calls = BasicDependencyAnalysis.Analyze(source, path);

        Assert.Equal(new[] { "beforecall", "childcall", "defaultvalue", "innercall", "indexcall", "aftercall" }, calls.Select(call => call.Name));
        Assert.Equal(new[] { path, child, child, child, path, path }, calls.Select(call => call.SourcePath));
    }

    [Fact]
    public void DetailedAnalysisReportsTheWholeSourceClosureIncludingEmptyIncludes()
    {
        var path = Path.Combine(directory, "main.bb");
        var child = Path.Combine(directory, "child.bb");
        var empty = Path.Combine(directory, "empty.bb");
        File.WriteAllText(child, "Include \"empty.bb\"\nChildCall\n");
        File.WriteAllText(empty, "");

        var result = BasicDependencyAnalysis.AnalyzeDetailed("Include \"child.bb\"\nInclude \"EMPTY.BB\"\n", path);

        Assert.Equal(new[] { path, child, empty }, result.SourcePaths);
        Assert.Equal("childcall", Assert.Single(result.CallSites).Name);
    }

    [Fact]
    public void SkipsCompilerIntrinsicsCSharpAndArrayReferencesButVisitsTheirArguments()
    {
        var source = """
            Dim items(4)
            items(TargetIndex())=ValueCall()
            Print items(ReadIndex())
            Print Int(ConversionInput()) + Float#(OtherInput())
            Print Str$(TextInput())
            Print Abs(UnaryInput()) + Sgn(SignInput())
            CS::System.Console.WriteLine(BridgeInput())
            Print CS::System.Math.Max(BridgeExpression(), 0)
            <?cs ArbitraryCall(); ?>
            """;

        var calls = BasicDependencyAnalysis.Analyze(source, Path.Combine(directory, "main.bb"));

        Assert.Equal(new[] { "targetindex", "valuecall", "readindex", "conversioninput", "otherinput", "textinput", "unaryinput", "signinput", "bridgeinput", "bridgeexpression" }, calls.Select(call => call.Name));
        Assert.All(calls, call => Assert.False(call.UsedAsStatement));
    }

    [Fact]
    public void TraversesControlFlowDeclarationsAndObjectExpressionsWithoutSemanticValidation()
    {
        var source = """
            Type Node
            Field value%=FieldDefault(), slots%[FieldBound()]
            End Type
            Local v%[VectorBound()]
            Global result=Initializer()
            Const broken=ConstantCall()
            Data DataCall()
            Read v[ReadIndex()]
            If Condition()
              ThenCall
            Else
              ElseCall
            EndIf
            While WhileCondition()
              WhileBody
            Wend
            Repeat
              RepeatBody
            Until UntilCondition()
            For v[ForIndex()]=StartCall() To EndCall() Step StepCall()
              ForBody
            Next
            For node.Node=Each Node
              ForEachBody
            Next
            Select Selector()
            Case CaseValue()
              CaseBody
            Default
              DefaultBody
            End Select
            node\value=FieldValue()
            v[WriteIndex()]=VectorValue()
            Delete Object.Node ObjectHandle()
            Insert Object.Node InsertHandle() Before Object.Node AnchorHandle()
            """;

        var calls = BasicDependencyAnalysis.Analyze(source, Path.Combine(directory, "main.bb"));

        Assert.Equal(new[] { "fielddefault", "fieldbound", "vectorbound", "initializer", "constantcall", "datacall", "readindex", "condition", "thencall", "elsecall", "whilecondition", "whilebody", "repeatbody", "untilcondition", "forindex", "startcall", "endcall", "stepcall", "forbody", "foreachbody", "selector", "casevalue", "casebody", "defaultbody", "fieldvalue", "writeindex", "vectorvalue", "objecthandle", "inserthandle", "anchorhandle" }, calls.Select(call => call.Name));
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.ValidateSemantics(source, Path.Combine(directory, "main.bb")));
    }

    [Theory]
    [InlineData("Print @")]
    [InlineData("Print 1+")]
    [InlineData("Include \"missing.bb\"")]
    public void PreservesSyntaxAndIncludeFailures(string source)
        => Assert.Throws<BasicDiagnosticException>(() => BasicDependencyAnalysis.Analyze(source, Path.Combine(directory, "main.bb")));

    public void Dispose() => Directory.Delete(directory, true);
}
