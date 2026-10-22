using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using BlitzCS.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BlitzCS.Compiler.Tests;

[CollectionDefinition("Object memory measurements", DisableParallelization = true)]
public sealed class ObjectMemoryCollection { }

[Collection("Object memory measurements")]
public sealed class ObjectMemoryTests
{
    private const int ChurnCount = 100_000;

    [Fact]
    public void DeletedAliasRemainsReachableAndInvalidButIsNotAListRoot()
    {
        ArrayCompilerTests.WithAssembly("""
Type Node
 Field value%
End Type
Global alias.Node
Global found.Node
Local node.Node=New Node
node\value=42
alias=node
Delete node
found=First Node
""", assembly =>
        {
            var bb = assembly.GetType("BB")!;
            var alias = bb.GetField("alias", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.NotNull(alias);
            Assert.False((bool)alias!.GetType().GetField("Alive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(alias)!);
            var objectTypes = assembly.GetType("__BlitzObjects") ?? throw new Xunit.Sdk.XunitException(
                "Generated object registry missing: " + string.Join(", ", assembly.GetTypes().Select(t => t.FullName)));
            var nodeList = objectTypes.GetField("node", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new Xunit.Sdk.XunitException("Generated registry fields: " + string.Join(", ", objectTypes.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.Name)));
            var list = nodeList.GetValue(null)!;
            Assert.NotNull(list.GetType().GetField("head", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(list));
            Assert.Null(bb.GetField("found", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        });
    }

    [Fact]
    public void DeletedObjectIsCollectibleAfterItsLastLanguageAliasIsReleased()
    {
        ArrayCompilerTests.WithAssembly("""
Type Node
 Field value%
End Type
Global alias.Node
Local node.Node=New Node
alias=node
Delete node
""", assembly =>
        {
            var weak = CaptureAndReleaseAlias(assembly);
            ForceFullCollection();
            Assert.False(weak.IsAlive);
        });
    }

    [Fact]
    public void DeletedCircularObjectsAreCollectibleAfterFieldsAndAliasesAreCleared()
    {
        ArrayCompilerTests.WithAssembly("""
Type Node
 Field link.Node
End Type
Global keeper.Node
Global witness.Node
Local a.Node=New Node
Local b.Node=New Node
a\link=b
b\link=a
keeper=a
witness=b
Delete a
Delete b
""", assembly =>
        {
            var weak = CaptureAndReleaseAlias(assembly, "witness");
            var keeper = assembly.GetType("BB")!.GetField("keeper", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.False((bool)keeper.GetType().GetField("Alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(keeper)!);
            Assert.Null(keeper.GetType().GetField("link", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(keeper));
            ForceFullCollection();
            Assert.False(weak.IsAlive);
        });
    }

    [Fact]
    public void DeletedObjectStoredInArrayLivesUntilTheArrayAliasIsCleared()
    {
        ArrayCompilerTests.WithAssembly("""
Type Node
 Field value%
End Type
Dim references.Node(0)
Local node.Node=New Node
node\value=42
references(0)=node
Delete node
""", assembly =>
        {
            var weak = CaptureAndReleaseArrayElement(assembly);
            ForceFullCollection();
            Assert.False(weak.IsAlive);
        });
    }

    [Fact]
    public void ObjectChurnReportsAllocationLiveHeapAndGcCommittedBytes()
    {
        var snapshot = RunChurnAndMeasure(ChurnCount);
        WriteSnapshot(snapshot.BaselineRuntime ? "memory-baseline-comparable.json" : "memory-after.json", snapshot);

        Assert.Equal(ChurnCount, snapshot.Created);
        Assert.Equal(ChurnCount, snapshot.Deleted);
        if (snapshot.BaselineRuntime)
        {
            Assert.Equal(ChurnCount, snapshot.StrongDeletedReferencesBeforeCollection);
            Assert.Equal(ChurnCount, snapshot.ListedAfterFullCollectionBeforeScavenge);
            Assert.Equal(ChurnCount, snapshot.ListedAfterScavengeOrUnchanged);
        }
        else
        {
            Assert.Equal(0, snapshot.StrongDeletedReferencesBeforeCollection);
            Assert.Equal(0, snapshot.DeletedWeakTargetsAfterFullCollection);
            Assert.Equal(0, snapshot.ListedAfterIncrementalMaintenance);
            Assert.Equal(0, snapshot.ListedAfterScavengeOrUnchanged);
        }
        Assert.Equal(0, snapshot.AliveAfterFullCollection);
        Assert.True(snapshot.CurrentThreadAllocatedBytes > 0);
    }

    private static MemorySnapshot RunChurnAndMeasure(int count)
    {
        var source = $$"""
Type Node
 Field value%
End Type
For i%=1 To {{count}}
 Local node.Node=New Node
 node\value=i
 Delete node
Next
""";
        var code = BasicCompiler.Compile(source, "object-memory.bb");
        var baselineRuntime = Environment.GetEnvironmentVariable("BLITZCS_MEMORY_RUNTIME_SOURCE");
        if (!string.IsNullOrWhiteSpace(baselineRuntime))
        {
            var legacyRuntime = File.ReadAllText(baselineRuntime);
            var runtimeStart = code.IndexOf("internal abstract class __BlitzObject\n", StringComparison.Ordinal);
            var generatedTypeStart = code.IndexOf("internal sealed class __BlitzObject_", runtimeStart, StringComparison.Ordinal);
            Assert.True(runtimeStart >= 0 && generatedTypeStart > runtimeStart, "Could not locate generated object runtime boundaries.");
            code = code[..runtimeStart] + legacyRuntime + "\n" + code[generatedTypeStart..];
        }
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(code)], references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        stream.Position = 0;
        return InvokeAndMeasure(stream, count, !string.IsNullOrWhiteSpace(baselineRuntime));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MemorySnapshot InvokeAndMeasure(MemoryStream stream, int count, bool baselineRuntime)
    {
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var objectTypes = assembly.GetType("__BlitzObjects")!;
            var list = objectTypes.GetField("node", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var currentThreadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            assembly.EntryPoint!.Invoke(null, null);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var currentThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - currentThreadAllocatedBefore;
            var listedBeforeCollection = CountListNodes(list);
            var aliveBeforeCollection = CountAliveNodes(list);
            var strongDeleted = CountStrongDeletedReferences(list);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var weakTargetsAfterCollection = CountDeletedWeakTargets(list);
            var listedAfterCollection = CountListNodes(list);
            // Delete(null) performs the normal bounded maintenance without adding
            // new positions; two visits per call cover the whole post-GC list.
            var maintenanceRounds = baselineRuntime ? 0 : listedAfterCollection / 2 + 1;
            if (!baselineRuntime) ExerciseIncrementalMaintenance(list, maintenanceRounds);
            var listedAfterIncrementalMaintenance = CountListNodes(list);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            list.GetType().GetMethod("Scavenge", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(list, null);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var gc = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var liveBytes = GC.GetTotalMemory(forceFullCollection: true);
            var aliveAfterCollection = CountAliveNodes(list);
            var listedAfterScavenge = CountListNodes(list);
            return new(baselineRuntime, count, count, listedBeforeCollection, aliveBeforeCollection, strongDeleted,
                weakTargetsAfterCollection, listedAfterCollection, maintenanceRounds, listedAfterIncrementalMaintenance,
                listedAfterScavenge, aliveAfterCollection, allocatedBytes, liveBytes,
                currentThreadAllocatedBytes,
                gc.HeapSizeBytes, gc.FragmentedBytes, gc.TotalCommittedBytes,
                gc.TotalAvailableMemoryBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }
        finally
        {
            context.Unload();
        }
    }

    private static int CountListNodes(object list)
    {
        var node = list.GetType().GetField("head", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(list);
        var next = node?.GetType().GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var count = 0;
        while (node is not null)
        {
            count++;
            node = next!.GetValue(node);
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureAndReleaseAlias(Assembly assembly, string global = "alias")
    {
        var alias = assembly.GetType("BB")!.GetField(global, BindingFlags.Static | BindingFlags.NonPublic)!;
        var target = alias.GetValue(null)!;
        var weak = new WeakReference(target);
        alias.SetValue(null, null);
        var nodeList = assembly.GetType("__BlitzObjects")!
            .GetField("node", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var scavenge = nodeList.GetType().GetMethod("Scavenge", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        scavenge?.Invoke(nodeList, null);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureAndReleaseArrayElement(Assembly assembly)
    {
        var arrays = assembly.GetType("__BlitzArrays")!;
        var array = arrays.GetField("a0", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        var data = (Array)array.GetType().GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(array)!;
        var target = data.GetValue(0)!;
        Assert.False((bool)target.GetType().GetField("Alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!);
        var weak = new WeakReference(target);
        data.SetValue(null, 0);
        var nodeList = assembly.GetType("__BlitzObjects")!
            .GetField("node", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(null)!;
        nodeList.GetType().GetMethod("Scavenge", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(nodeList, null);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static int CountAliveNodes(object list)
    {
        var node = list.GetType().GetField("head", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(list);
        var type = node?.GetType();
        var next = type?.GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var alive = type?.GetField("Alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var count = 0;
        while (node is not null)
        {
            var value = type?.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(node);
            if (value is not null)
            {
                var valueAlive = value.GetType().GetField("Alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
                if ((bool)valueAlive.GetValue(value)!) count++;
            }
            else if (alive is not null && (bool)alive.GetValue(node)!) count++;
            node = next!.GetValue(node);
        }
        return count;
    }

    private static int CountStrongDeletedReferences(object list)
    {
        var node = list.GetType().GetField("head", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(list);
        var type = node?.GetType();
        var next = type?.GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var valueField = type?.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var count = 0;
        while (node is not null)
        {
            object? value;
            if (valueField is not null) value = valueField.GetValue(node);
            else value = type?.GetField("Alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null ? null : node;
            if (value is not null && !(bool)value.GetType().GetField("Alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!)
                count++;
            node = next!.GetValue(node);
        }
        return count;
    }

    private static int CountDeletedWeakTargets(object list)
    {
        var node = list.GetType().GetField("head", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(list);
        var type = node?.GetType();
        var next = type?.GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var deletedField = type?.GetField("Deleted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (deletedField is null) return 0;
        var count = 0;
        while (node is not null)
        {
            var weak = deletedField.GetValue(node);
            if (weak is not null)
            {
                var arguments = new object?[] { null };
                if ((bool)weak.GetType().GetMethod("TryGetTarget")!.Invoke(weak, arguments)!) count++;
            }
            node = next!.GetValue(node);
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExerciseIncrementalMaintenance(object list, int rounds)
    {
        var type = list.GetType();
        var delete = type.GetMethod("Delete", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        for (var i = 0; i < rounds; i++)
            delete.Invoke(list, [null]);
    }

    private static void WriteSnapshot(string fileName, MemorySnapshot snapshot)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".trail"))) root = root.Parent;
        if (root is null) return;
        var artifacts = Path.Combine(root.FullName, "artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, fileName), JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record MemorySnapshot(
        bool BaselineRuntime,
        int Created,
        int Deleted,
        int ListedBeforeFullCollection,
        int AliveBeforeFullCollection,
        int StrongDeletedReferencesBeforeCollection,
        int DeletedWeakTargetsAfterFullCollection,
        int ListedAfterFullCollectionBeforeScavenge,
        int IncrementalMaintenanceRounds,
        int ListedAfterIncrementalMaintenance,
        int ListedAfterScavengeOrUnchanged,
        int AliveAfterFullCollection,
        long ProcessAllocatedBytes,
        long LiveAfterFullCollectionBytes,
        long CurrentThreadAllocatedBytes,
        long GcHeapBytes,
        long GcFragmentedBytes,
        long GcCommittedBytes,
        long GcAvailableBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);
}
