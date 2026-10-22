using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BlitzCS.Compiler.Tests;

public sealed class FixedVectorLifetimeTests
{
    [Fact]
    public void DeletingParentClearsVectorReferencesWhilePreservingDeletedParentAlias()
    {
        ArrayCompilerTests.WithAssembly("""
            Type Child
                Field value%
            End Type
            Type Holder
                Field children.Child[1]
            End Type
            Global holder.Holder=New Holder
            Global witness.Child=New Child
            holder\children[1]=witness
            Delete witness
            Function DeleteOwner%()
                Delete holder
                Return 0
            End Function
            """, assembly =>
        {
            var weak = ReleaseWitnessAlias(assembly);
            Collect();
            Assert.True(weak.IsAlive); // The live parent's vector is still an observable alias.
            var bb = assembly.GetType("BB")!;
            bb.GetMethod("deleteowner", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
            Collect();
            Assert.False(weak.IsAlive);
            var parent = bb.GetField("holder", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.False((bool)parent.GetType().GetField("Alive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parent)!);
            Assert.Null(parent.GetType().GetField("children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parent));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReleaseWitnessAlias(Assembly assembly)
    {
        var field = assembly.GetType("BB")!.GetField("witness", BindingFlags.Static | BindingFlags.NonPublic)!;
        var weak = new WeakReference(field.GetValue(null)!);
        field.SetValue(null, null);
        return weak;
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
