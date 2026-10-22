using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class HandleLifetimeTests
{
    private static IDictionary Table(Assembly assembly) =>
        (IDictionary)assembly.GetType("__BlitzHandles")!.GetField("live", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [Fact]
    public void HundredThousandHandleCyclesLeaveNoLiveEntriesOrReusedIdentifiers()
    {
        ArrayCompilerTests.WithAssembly("""
            Type Node
              Field link.Node
            End Type
            Global lastHandle%, failures%
            For i=1 To 100000
              n.Node=New Node
              lastHandle=Handle(n)
              check.Node=Object.Node(lastHandle)
              If check<>n Then failures=failures+1
              n\link=n
              Delete n
              check=Object.Node(lastHandle)
              If check<>Null Then failures=failures+1
            Next
            Delete Null
            """, assembly =>
        {
            var bb = assembly.GetType("BB")!;
            Assert.Equal(100000, bb.GetField("lasthandle")!.GetValue(null));
            Assert.Equal(0, bb.GetField("failures")!.GetValue(null));
            var table = Table(assembly);
            Assert.Empty(table);
            // Reserved dictionary storage follows peak simultaneous handles (one), not total operations.
            var capacity = (int)table.GetType().GetMethod("EnsureCapacity")!.Invoke(table, [0])!;
            Assert.InRange(capacity, 1, 16);
            GC.KeepAlive(assembly);
        });
    }

    [Fact]
    public void IntegerHandlesDoNotRootDeletedObjectsAndLiveObjectsStillResolve()
    {
        ArrayCompilerTests.WithAssembly("""
            Type Node
              Field link.Node
            End Type
            Global witness.Node=New Node
            witness\link=witness
            Global number%=Handle(witness)
            Function Remove%()
              Delete witness
              Return 0
            End Function
            """, assembly =>
        {
            var bb = assembly.GetType("BB")!;
            Assert.Single(Table(assembly));
            var weak = DeleteAndReleaseWitness(assembly);
            Assert.Empty(Table(assembly));
            Collect();
            Assert.False(weak.IsAlive);
            Assert.Equal(1, bb.GetField("number")!.GetValue(null));
            GC.KeepAlive(assembly); // Registry/list stay rooted while the target must be collectible.
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DeleteAndReleaseWitness(Assembly assembly)
    {
        var bb = assembly.GetType("BB")!;
        var field = bb.GetField("witness", BindingFlags.Static | BindingFlags.NonPublic)!;
        var weak = new WeakReference(field.GetValue(null)!);
        bb.GetMethod("remove")!.Invoke(null, null);
        field.SetValue(null, null);
        return weak;
    }

    [Fact]
    public void ExhaustionFailsBeforeReusingOrRegisteringAnIdentifier()
    {
        ArrayCompilerTests.WithAssembly("""
            Type Node
            End Type
            Global witness.Node=New Node
            Function MakeHandle%()
              Return Handle(witness)
            End Function
            """, assembly =>
        {
            assembly.GetType("__BlitzHandles")!.GetField("next", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, int.MaxValue);
            var exception = Assert.Throws<TargetInvocationException>(() => assembly.GetType("BB")!.GetMethod("makehandle")!.Invoke(null, null));
            Assert.Contains("identifiers exhausted", exception.InnerException!.Message);
            Assert.Empty(Table(assembly));
        });
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
